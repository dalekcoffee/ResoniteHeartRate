using ResoniteModLoader;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ResoniteHeartRate {

    // Connects to Pulsoid's free real-time heart-rate websocket so the mod works
    // without the paid (BRO) REST polling plan that /data/heart_rate/latest requires.
    //
    //   wss://dev.pulsoid.net/api/v1/data/real_time?access_token=<token>&response_mode=legacy_json
    //
    // NOTE: this uses the runtime's built-in System.Net.WebSockets.ClientWebSocket rather than
    // websocket-sharp-core. The websocket-sharp library cannot talk to Pulsoid's server
    // (akka-http) - it closes with protocol error 1002 / never delivers data frames - whereas
    // ClientWebSocket connects and streams correctly. The access token is the user's own
    // Pulsoid key, read from mod config - never hardcoded.
    internal class PulsoidWebSocket {

        private const string ENDPOINT = "wss://dev.pulsoid.net/api/v1/data/real_time";

        private readonly ClientWebSocket _ws = new ClientWebSocket();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private volatile int _heartRate = 0;
        // Stays "alive" while connecting or connected; flips to faulted once the receive
        // loop ends (close / error), so the poll loop knows when to spin up a fresh socket.
        private volatile bool _faulted = false;

        public PulsoidWebSocket(string accessToken) {

            if (string.IsNullOrWhiteSpace(accessToken)) {
                ResoniteMod.Error("[Pulsoid] No Pulsoid key set in mod settings; cannot connect.");
                _faulted = true;
                return;
            }

            // Stray whitespace from pasting would be URL-encoded into the token and rejected (401).
            accessToken = accessToken.Trim();

            // Masked diagnostic so the log reveals WHICH token is in use without exposing the secret.
            // A 401 with the wrong prefix/length here means the Pulsoid Key is not your real-time token.
            string masked = accessToken.Length <= 8
                ? "(too short)"
                : accessToken.Substring(0, 4) + "..." + accessToken.Substring(accessToken.Length - 4);
            ResoniteMod.Msg($"[Pulsoid] Connecting with token len={accessToken.Length} ({masked}). " +
                            "If this 401s, the Pulsoid Key is the wrong token - use your real-time token.");

            string url = ENDPOINT
                + "?access_token=" + Uri.EscapeDataString(accessToken)
                + "&response_mode=legacy_json";

            // Connect + receive on a background thread; the mod polls getHeartRate() from its
            // own HR thread.
            Task.Run(() => ReceiveLoop(url));
        }

        private async Task ReceiveLoop(string url) {

            byte[] buffer = new byte[8192];
            try {
                await _ws.ConnectAsync(new Uri(url), _cts.Token).ConfigureAwait(false);
                ResoniteMod.Msg("[Pulsoid] Websocket connected.");

                while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested) {

                    StringBuilder sb = new StringBuilder();
                    WebSocketReceiveResult result;
                    do {
                        result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) {
                            ResoniteMod.Msg($"[Pulsoid] Server closed: {result.CloseStatus} {result.CloseStatusDescription}");
                            return;
                        }
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    } while (!result.EndOfMessage);

                    ParseHeartRate(sb.ToString());
                }
            }
            catch (OperationCanceledException) {
                // Normal shutdown via Close().
            }
            catch (Exception ex) {
                ResoniteMod.Error("[Pulsoid] Websocket error: " + ex.Message);
            }
            finally {
                _faulted = true;
            }
        }

        private void ParseHeartRate(string data) {

            // Pulsoid legacy_json frames look like:
            //   {"timestamp":1782694167485,"data":{"heartRate":100}}
            // The key is camelCase "heartRate". Locate it and read the first integer that
            // follows (the key name has no digits); also accept a snake_case "heart_rate" variant.
            try {
                if (string.IsNullOrEmpty(data)) return;

                int idx = data.IndexOf("heartRate", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) idx = data.IndexOf("heart_rate", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return;

                int i = idx;
                while (i < data.Length && (data[i] < '0' || data[i] > '9')) i++;
                int start = i;
                while (i < data.Length && data[i] >= '0' && data[i] <= '9') i++;

                if (i > start && int.TryParse(data.Substring(start, i - start), out int hr)) {
                    _heartRate = hr;
                }
            }
            catch (Exception ex) {
                ResoniteMod.Error("[Pulsoid] Error parsing message: " + ex.Message);
            }
        }

        public int getHeartRate() { return _heartRate; }

        public bool isAlive() { return !_faulted; }

        public void Close() {
            try {
                _cts.Cancel();
                if (_ws.State == WebSocketState.Open) {
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None)
                       .GetAwaiter().GetResult();
                }
            }
            catch { /* ignore close errors */ }
            finally {
                _faulted = true;
                try { _ws.Dispose(); } catch { }
                try { _cts.Dispose(); } catch { }
            }
        }
    }
}
