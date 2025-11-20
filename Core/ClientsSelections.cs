using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using plot_twist_back_end.Messages;
using plot_twist_back_end.Utils.Utils;

namespace plot_twist_back_end.Core;

public class ClientsSelections {
    private readonly Dictionary<int, ClientSelection> _selectionsPerClients = new Dictionary<int, ClientSelection>();
    private readonly object _selectionsLock = new object();
    private readonly object _throttleLock = new object();

    private WebSocketCoordinator _wsCoordinator;
    
    private long _lastUpdateTimestamp = 0;
    private int? _pendingSocketId = null;
    private System.Timers.Timer _throttleTimer;
    private const int ThrottleIntervalMs = 70; 
    private CrossDataSetLinks _links;
    private bool _isUpdating = false;

    // ensure only one broadcast executes at a time to avoid piling up
    private readonly SemaphoreSlim _broadcastSemaphore = new SemaphoreSlim(1, 1);

    public ClientsSelections(WebSocketCoordinator wsCoordinator, CrossDataSetLinks links) {
        this._wsCoordinator = wsCoordinator;
        this._links = links;
    }
    
    public void AddClient(int socketId) {
        lock (_selectionsLock)
        {
            _selectionsPerClients[socketId] = new ClientSelection();
        }

        // fire-and-forget broadcast (don't block caller)
        _ = BroadcastClientsSelections(socketId);
    }

    public void RemoveClient(int socketId) {
        lock (_selectionsLock)
        {
            _selectionsPerClients.Remove(socketId);
        }

        _ = BroadcastClientsSelections(0);
    }

    public void UpdateClientSelection(int socketId, ClientSelection socketSelection) {
        if (socketId == 0) return;

        lock (_selectionsLock)
        {
            _selectionsPerClients[socketId] = socketSelection;
        }
    }
    
    public void ThrottledBroadcastClientsSelections(int socketId)
    {
        int? immediateSend = null;

        lock (_throttleLock)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = (_lastUpdateTimestamp == 0)
                ? long.MaxValue // force immediate if never updated before
                : (now - _lastUpdateTimestamp) * 1000 / Stopwatch.Frequency;

            if (elapsedMs >= ThrottleIntervalMs)
            {
                // send immediately (but outside lock)
                _lastUpdateTimestamp = now;
                immediateSend = socketId;
            }
            else
            {
                // schedule latest socketId
                _pendingSocketId = socketId;

                if (_throttleTimer == null)
                {
                    _throttleTimer = new System.Timers.Timer();
                    _throttleTimer.AutoReset = false;
                    _throttleTimer.Elapsed += (_, __) =>
                    {
                        int? pendingToSend = null;

                        lock (_throttleLock)
                        {
                            // stop early (defensive)
                            try { _throttleTimer.Stop(); } catch { /* ignore */ }

                            if (_pendingSocketId.HasValue)
                            {
                                pendingToSend = _pendingSocketId;
                                _pendingSocketId = null;
                                _lastUpdateTimestamp = Stopwatch.GetTimestamp();
                            }
                        }

                        // call outside lock
                        if (pendingToSend.HasValue)
                        {
                            _ = BroadcastClientsSelections(pendingToSend.Value);
                        }
                    };
                }

                // compute remaining interval and restart timer
                var remaining = ThrottleIntervalMs - (int)elapsedMs;
                if (remaining < 1) remaining = 1;
                _throttleTimer.Interval = remaining;
                _throttleTimer.Stop();
                _throttleTimer.Start();
            }
        }

        if (immediateSend.HasValue)
        {
            _ = BroadcastClientsSelections(immediateSend.Value);
        }
    }

    /// <summary>
    /// Broadcast current global crossSelections and per-client selection messages.
    /// This method is robust: it serializes broadcasts with a semaphore and
    /// protects each individual send with try/catch and a timeout.
    /// </summary>
    public async Task BroadcastClientsSelections(int socketId)
    {
        // ensure only one broadcast at a time
        await _broadcastSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            // Take a snapshot of the selections so we can iterate without locking
            Dictionary<int, ClientSelection> snapshot;
            lock (_selectionsLock)
            {
                snapshot = _selectionsPerClients.ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            // Build global selectionSet from snapshot
            selectionSet globalSelections = new selectionSet();
            foreach (var (_, selection) in snapshot)
            {
                globalSelections.AddSelectionArr(selection.selectionPerDataSet);
            }

            var timer = Stopwatch.StartNew();
            {
                var selectionPerDataset = globalSelections.ToArr();
                if (selectionPerDataset != null)
                {
                    foreach (var selection in selectionPerDataset)
                    {
                        if (selection!.dataSetName != null && selection.indexesSelected != null)
                        {
                            _links.updateDataSetSelection(selection.dataSetName, selection.indexesSelected.ToList());
                        }
                    }
                }

                _links.updateCrossDataSetSelection();

                var crossSelections = _links.getCrossSelections();

                // send crossSelection to each client (based on snapshot keys) in parallel but protected
                var crossSendTasks = new List<Task>();
                foreach (var (id, _) in snapshot)
                {
                    var msg = new Message
                    {
                        type = "crossSelection",
                        dataSetCrossSelection = crossSelections.ToArray()
                    };

                    crossSendTasks.Add(SendToClientWithTimeout(msg, id, false));
                }

                // Wait for cross-selection sends to finish (or time out)
                await Task.WhenAll(crossSendTasks).ConfigureAwait(false);
            }
            timer.Stop();

            // For each client, build client-specific selection set (excluding that client)
            var snapshotKeys = snapshot.Keys.ToList();
            // send per-client selection messages in parallel with per-send try/catch
            var sendTasks = new List<Task>();
            foreach (var id in snapshotKeys)
            {
                if (id == socketId) continue;

                var clientSelections = new selectionSet();
                foreach (var (id2, selection) in snapshot)
                {
                    if (id == id2) continue;
                    clientSelections.AddSelectionArr(selection.selectionPerDataSet);
                }

                var clientSelection = new ClientSelection { selectionPerDataSet = clientSelections.ToArr() };
                var msg = new Message
                {
                    type = "selection",
                    clientsSelections = new ClientSelection[] { clientSelection }
                };

                sendTasks.Add(SendToClientWithTimeout(msg, id));
            }

            await Task.WhenAll(sendTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // keep broadcast errors logged but don't let them bring down the server.
            Console.WriteLine($"[BroadcastClientsSelections] Unexpected exception: {ex}");
        }
        finally
        {
            _broadcastSemaphore.Release();
        }
    }

    /// <summary>
    /// Helper to send a message to client with a timeout and error handling.
    /// The timeout does not cancel the underlying send but will log if it exceeds the threshold.
    /// </summary>
    private async Task SendToClientWithTimeout(Message msg, int clientId, bool useReliable = true)
    {
        try
        {
            var SendTimeoutMs = 100;
            var sendTask = _wsCoordinator.SendMessageToClient(msg, clientId, useReliable);
            // wait with timeout
            var completed = await Task.WhenAny(sendTask, Task.Delay(SendTimeoutMs)).ConfigureAwait(false);
            if (completed != sendTask)
            {
                // timeout happened
                Console.WriteLine($"[SendToClientWithTimeout] send to {clientId} timed out after {SendTimeoutMs} ms (message type: {msg.type}).");
                // allow the original task to continue in background; don't await it here to avoid hanging.
            }
            else
            {
                // await to propagate any exceptions
                await sendTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SendToClientWithTimeout] Failed sending '{msg.type}' to {clientId}: {ex}");
        }
    }
}
