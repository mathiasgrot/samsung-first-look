using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Text WebSocket client that keeps itself connected: once Open() is called it retries every
/// reconnectDelay seconds while the component is enabled. Knows nothing about what the messages mean.
/// Awaits resume on the main thread (Unity's SynchronizationContext), so the events fire there too.
/// </summary>

public class WebSocketClient : MonoBehaviour
{
    #region VARIABLES

    [SerializeField] private float reconnectDelay = 2f;
    [Tooltip("Set by Open(). TouchDesignerBridge passes Config.json's serverUrl.")]
    [SerializeField] private string url;

    public bool IsConnected => socket != null && socket.State == WebSocketState.Open;

    private bool opened;  // Open() was called, so re-enabling reconnects
    private ClientWebSocket socket;
    private CancellationTokenSource cts;
    private readonly SemaphoreSlim sendLock = new SemaphoreSlim(1, 1);

    #endregion

    #region EVENTS

    public event Action Connected;
    public event Action Disconnected;  // only after a connection that was open, not on failed attempts
    public event Action<string> MessageReceived;

    #endregion

    #region LIFECYCLE

    // Reconnects when re-enabled after Open() was called.
    void OnEnable()
    {
        if (opened)
            StartLoop();
    }

    void OnDisable()
    {
        StopLoop();
    }

    #endregion

    #region CONTROL

    // Connects to _url and keeps the connection up. Calling it again switches to the new url.
    public void Open(string _url)
    {
        url = _url;
        opened = true;
        StopLoop();
        if (isActiveAndEnabled)
            StartLoop();
    }

    public async void Send(string _text)
    {
        ClientWebSocket ws = socket;
        if (ws == null || ws.State != WebSocketState.Open)
        {
            Debug.Log($"[WebSocketClient] Not connected, dropping {_text}", this);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(_text);
        CancellationToken token = cts?.Token ?? CancellationToken.None;

        // ClientWebSocket doesn't allow overlapping sends.
        await sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Debug.LogWarning($"[WebSocketClient] Send failed: {e.Message}", this);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private void StartLoop()
    {
        if (cts != null)
            return;
        cts = new CancellationTokenSource();
        _ = ConnectionLoop(url, cts.Token);  // fire and forget: the loop catches its own exceptions
    }

    private void StopLoop()
    {
        // Not disposed on purpose: the connection loop may still be reading the token while it unwinds.
        cts?.Cancel();
        cts = null;
    }

    #endregion

    #region CONNECTION

    private async Task ConnectionLoop(string _url, CancellationToken _token)
    {
        bool loggedFailure = false;

        while (!_token.IsCancellationRequested)
        {
            using (var ws = new ClientWebSocket())
            {
                bool wasConnected = false;
                try
                {
                    await ws.ConnectAsync(new Uri(_url), _token);
                    socket = ws;
                    wasConnected = true;
                    loggedFailure = false;

                    Debug.Log($"[WebSocketClient] Connected to {_url}", this);
                    Connected?.Invoke();

                    await ReceiveLoop(ws, _token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    // Log once per outage instead of on every retry.
                    if (!loggedFailure)
                    {
                        Debug.LogWarning($"[WebSocketClient] Can't reach {_url} ({e.Message}). Retrying every {reconnectDelay}s.", this);
                        loggedFailure = true;
                    }
                }
                finally
                {
                    if (socket == ws)
                        socket = null;
                    if (wasConnected)
                    {
                        Debug.Log("[WebSocketClient] Disconnected", this);
                        if (!_token.IsCancellationRequested)
                            Disconnected?.Invoke();
                    }
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(reconnectDelay), _token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Collects message fragments and hands each complete text message on.
    private async Task ReceiveLoop(ClientWebSocket _ws, CancellationToken _token)
    {
        var buffer = new ArraySegment<byte>(new byte[8192]);

        using (var message = new MemoryStream())
        {
            while (_ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await _ws.ReceiveAsync(buffer, _token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    return;
                }

                message.Write(buffer.Array, 0, result.Count);

                if (!result.EndOfMessage)
                    continue;

                if (result.MessageType == WebSocketMessageType.Text)
                    MessageReceived?.Invoke(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));

                message.SetLength(0);
            }
        }
    }

    #endregion
}
