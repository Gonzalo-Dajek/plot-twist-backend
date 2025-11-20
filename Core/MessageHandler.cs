using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using plot_twist_back_end.Messages;

namespace plot_twist_back_end.Core
{
    public class MessageHandler
    {
        // Compile-time toggle (kept for compatibility; queue now always evicts oldest selections to preserve newest).
        private const bool DROP_HEAD_ON_OVERFLOW = false;

        public ClientsSelections selections { get; }
        public CrossDataSetLinks links { get; }
        public WebSocketCoordinator wsCoordinator { get; }
        public Benchmark benchmark;

        // per-socket custom bounded queue that preserves the last selection
        private readonly ConcurrentDictionary<int, BoundedPreserveLastQueue> _selectionQueues = new();
        private readonly ConcurrentDictionary<int, Task> _processingTasks = new();

        // small capacity to preserve last few selection messages
        private const int QueueCapacity = 2;
        // how long to wait for a SendMessageToClient before reporting a timeout (ms)
        private const int SendTimeoutMs = 5000;

        public MessageHandler(
            ClientsSelections selections,
            CrossDataSetLinks links,
            WebSocketCoordinator wsCoordinator,
            Benchmark benchmark)
        {
            this.selections = selections;
            this.links = links;
            this.wsCoordinator = wsCoordinator;
            this.benchmark = benchmark;
        }

        public async Task HandleMessage(string message, int socketId)
        {
            var clientMessage = JsonSerializer.Deserialize<Message>(message);
            if (clientMessage.type != "BenchMark")
            {
                // suppressed logging
            }

            switch (clientMessage.type)
            {
                case "link":
                    links.UpdateClientsLinks(clientMessage.links!, clientMessage.linksOperator!);
                    links.updateCrossDataSetSelection();
                    links.broadcastClientsLinks(socketId);
                    selections.ThrottledBroadcastClientsSelections(0);
                    break;

                case "selection":
                    if (_selectionQueues.TryGetValue(socketId, out var queue))
                    {
                        // Only selection messages are enqueued into per-client queues.
                        var enqueued = queue.EnqueuePreserveLast(clientMessage);
                        if (!enqueued)
                        {
                            Console.WriteLine($"[MessageHandler] EnqueuePreserveLast rejected for socket {socketId} (queue completed or disposed).");
                        }
                    }
                    else
                    {
                        // fallback immediate update if queue doesn't exist (client not initialized yet)
                        selections.UpdateClientSelection(socketId, clientMessage.clientsSelections![0]);
                        links.updateCrossDataSetSelection();
                        selections.ThrottledBroadcastClientsSelections(0);
                    }
                    break;

                case "addClient":
                    wsCoordinator.InitializeClient(socketId);
                    selections.AddClient(socketId);

                    var q = new BoundedPreserveLastQueue(QueueCapacity);
                    if (!_selectionQueues.TryAdd(socketId, q))
                    {
                        q.Dispose();
                    }
                    else
                    {
                        var task = Task.Run(() => ProcessSelectionQueueAsync(socketId, q));
                        if (!_processingTasks.TryAdd(socketId, task))
                        {
                            // unlikely, but if we failed to track it, at least log
                            Console.WriteLine($"[MessageHandler] Failed to add processing task tracking for socket {socketId}.");
                        }
                        // do not await here - background processing
                    }

                    links.broadcastClientsLinks();
                    selections.ThrottledBroadcastClientsSelections(0);
                    break;

                case "addDataSet":
                    links.AddDataset(clientMessage.dataSet![0]);
                    links.broadcastClientsLinks();
                    break;
            }
        }

        private async Task ProcessSelectionQueueAsync(int socketId, BoundedPreserveLastQueue queue)
        {
            Console.WriteLine($"[ProcessSelectionQueue] started for socket {socketId}.");
            try
            {
                while (true)
                {
                    var msg = await queue.DequeueAsync().ConfigureAwait(false);
                    if (msg is null)
                    {
                        Console.WriteLine($"[ProcessSelectionQueue] queue closed/empty for socket {socketId}; exiting.");
                        break; // completed & empty
                    }

                    try
                    {
                        long time = DateTimeOffset.Now.ToUnixTimeMilliseconds() - (msg.Value.clientsSelections![0].debugTimeSent ?? -1);
                        // Console.WriteLine($"Q_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}_{time}");
                        selections.UpdateClientSelection(socketId, msg.Value.clientsSelections![0]);
                        links.updateCrossDataSetSelection();
                        selections.ThrottledBroadcastClientsSelections(0);
                    }
                    catch (Exception ex)
                    {
                        // protect the loop from any unexpected exceptions caused by selection processing
                        Console.WriteLine($"[ProcessSelectionQueue] Exception while processing message for socket {socketId}: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProcessSelectionQueue] Exception in selection processor for {socketId}: {ex}");
            }
            finally
            {
                // do NOT dispose queue here. Disposal handled by closeChannel after waiting for this task to finish.
                Console.WriteLine($"[ProcessSelectionQueue] finished for socket {socketId}.");
            }
        }

        /// <summary>
        /// Close the per-socket queue and wait for its processor to finish, then dispose.
        /// Callers should await this to ensure graceful shutdown.
        /// </summary>
        public async Task closeChannel(int socketId)
        {
            if (_selectionQueues.TryRemove(socketId, out var q))
            {
                // signal no more items; wake consumer
                q.Complete();
                // wait for processing task to finish
                if (_processingTasks.TryRemove(socketId, out var task))
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // the task should protect itself, but log just in case
                        Console.WriteLine($"[closeChannel] processing task for {socketId} faulted: {ex}");
                    }
                }

                // now safe to dispose
                try
                {
                    q.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[closeChannel] Exception disposing queue for {socketId}: {ex}");
                }
            }
        }

        // --- internal helper queue class (selection-only queue) ---
        private class BoundedPreserveLastQueue : IDisposable
        {
            private readonly LinkedList<Message> _list = new();
            private readonly int _capacity;
            private readonly object _lock = new();
            private readonly SemaphoreSlim _items = new(0);
            private bool _completed = false;
            private bool _disposed = false;

            public BoundedPreserveLastQueue(int capacity)
            {
                _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            public bool EnqueuePreserveLast(Message msg)
            {
                if (msg.type != "selection")
                    throw new InvalidOperationException("Only 'selection' messages may be enqueued in BoundedPreserveLastQueue.");

                lock (_lock)
                {
                    if (_completed || _disposed) return false;

                    if (_list.Count < _capacity)
                    {
                        _list.AddLast(msg);
                        try
                        {
                            _items.Release();
                        }
                        catch (ObjectDisposedException)
                        {
                            // race with Dispose(): treat as failure
                            return false;
                        }
                        return true;
                    }

                    // Full: evict oldest selection(s) until there's room.
                    while (_list.Count >= _capacity)
                    {
                        // remove oldest
                        _list.RemoveFirst();
                        // try to decrement semaphore count to keep it consistent with the list.
                        // If Wait(0) returns false it means a consumer already took an item (so semaphore already decremented),
                        // so there's nothing to do.
                        try
                        {
                            _items.Wait(0);
                        }
                        catch
                        {
                            // ignore disposal or other races
                        }
                    }

                    // append the new (most recent) selection
                    _list.AddLast(msg);
                    try
                    {
                        _items.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        return false;
                    }
                    return true;
                }
            }

            public async Task<Message?> DequeueAsync()
            {
                // Loop: wait for a semaphore signal, then check list under lock.
                // If the list is empty and _completed is set -> return null (normal shutdown).
                // If the list is empty and not completed -> interpret as spurious wake and keep waiting.
                while (true)
                {
                    try
                    {
                        await _items.WaitAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // If disposed while waiting, treat as completed/empty.
                        return null;
                    }

                    lock (_lock)
                    {
                        if (_list.Count > 0)
                        {
                            var val = _list.First!.Value;
                            _list.RemoveFirst();
                            return val;
                        }

                        if (_completed)
                        {
                            // completed and no items left
                            return null;
                        }

                        // otherwise spurious wake: loop and wait again
                        continue;
                    }
                }
            }


            /// <summary>
            /// Mark complete: no more enqueue. Wakes consumers.
            /// </summary>
            public void Complete()
            {
                lock (_lock)
                {
                    if (_completed) return;
                    _completed = true;
                }
                // wake consumer so it can exit if queue empty
                try
                {
                    _items.Release();
                }
                catch
                {
                    // ignore race where semaphore already disposed
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    _items.Dispose();
                }
                catch
                {
                    // swallow disposal exceptions
                }
                lock (_lock) _list.Clear();
            }
        }
    }
}
