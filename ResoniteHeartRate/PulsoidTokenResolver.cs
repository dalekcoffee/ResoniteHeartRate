using ResoniteModLoader;
using System;
using System.IO;
using System.Net;
using System.Text;

namespace ResoniteHeartRate {

    // Turns whatever the user pastes into the "Pulsoid Key" field into the real-time access
    // token that PulsoidWebSocket needs. Accepts any of:
    //   * a full widget URL    https://pulsoid.net/widget/view/863818cc-...-935d
    //   * a bare widget ID     863818cc-b381-4cd0-af39-31ce2aa8935d
    //   * a raw access token   fee4496c-d0bd-4ce9-9785-dd187ad7124d  (the old behaviour)
    //
    // The widget-page itself resolves the ID into a token with one unauthenticated JSON-RPC
    // call to Pulsoid's public endpoint (captured from the live widget page):
    //
    //   POST https://pulsoid.net/v1/api/public/rpc
    //   {"jsonrpc":"2.0","method":"getWidget","params":{"widgetId":"<id>"},"id":"1"}
    //
    // ...whose result carries "token":"<access_token>". No login, no paid plan. We mirror that
    // so the user can just paste their widget URL instead of digging the token out of a HAR.
    internal static class PulsoidTokenResolver {

        private const string RPC_ENDPOINT = "https://pulsoid.net/v1/api/public/rpc";
        private const string WIDGET_PATH = "/widget/view/";

        // Returns the access token to hand to the websocket, or the trimmed input unchanged if
        // it already looks like a token (or if resolution fails - PulsoidWebSocket will then log
        // a clear 401 against the masked token, same as before).
        public static string Resolve(string input) {

            if (string.IsNullOrWhiteSpace(input)) return input;
            input = input.Trim();

            // Case 1: a widget URL - pull the ID out of the /widget/view/<id> path and resolve it.
            int p = input.IndexOf(WIDGET_PATH, StringComparison.OrdinalIgnoreCase);
            if (p >= 0) {
                // Take the path segment after /widget/view/, stopping at any query/fragment/slash.
                string id = input.Substring(p + WIDGET_PATH.Length).Split('?', '#', '/')[0];
                ResoniteMod.Msg("[Pulsoid] Widget URL detected; resolving widget ID -> access token.");
                string tok = ResolveWidgetId(id);
                return string.IsNullOrEmpty(tok) ? input : tok;
            }

            // Case 2: a bare GUID. It could be a widget ID OR an access token - they share the
            // GUID shape, so try resolving it as a widget ID. If Pulsoid says no such widget,
            // assume it was already a token and use it directly.
            if (LooksLikeGuid(input)) {
                string tok = ResolveWidgetId(input);
                if (!string.IsNullOrEmpty(tok)) {
                    ResoniteMod.Msg("[Pulsoid] Input was a widget ID; resolved it to an access token.");
                    return tok;
                }
                // Not a widget (or RPC failed) -> treat the input as the raw access token.
                return input;
            }

            // Anything else: pass through untouched.
            return input;
        }

        // Calls getWidget and returns result.token, or "" on any failure.
        private static string ResolveWidgetId(string widgetId) {

            if (string.IsNullOrWhiteSpace(widgetId)) return "";

            try {
                string body = "{\"jsonrpc\":\"2.0\",\"method\":\"getWidget\",\"params\":{\"widgetId\":\""
                              + widgetId.Trim() + "\"},\"id\":\"1\"}";
                byte[] payload = Encoding.UTF8.GetBytes(body);

                var req = (HttpWebRequest)WebRequest.Create(new Uri(RPC_ENDPOINT));
                req.Method = "POST";
                req.ContentType = "application/json";
                req.Headers["x-rpc-method"] = "getWidget";
                req.ContentLength = payload.Length;

                using (var reqStream = req.GetRequestStream()) {
                    reqStream.Write(payload, 0, payload.Length);
                }

                string json;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream())) {
                    json = reader.ReadToEnd();
                }

                // A JSON-RPC error response has no "result"/"token"; ExtractJsonString returns "".
                return ExtractJsonString(json, "token");
            }
            catch (Exception ex) {
                // 404/EntityNotFound is expected when the input is actually a raw token, so keep
                // this quiet (Debug, not Error) to avoid alarming logs in the normal token case.
                ResoniteMod.Debug("[Pulsoid] getWidget did not resolve a token: " + ex.Message);
                return "";
            }
        }

        // Minimal extractor for a top-level string field value: finds "<key>" then the next
        // double-quoted string after the following colon. Good enough for Pulsoid's flat token.
        private static string ExtractJsonString(string json, string key) {

            if (string.IsNullOrEmpty(json)) return "";

            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return "";

            int colon = json.IndexOf(':', k + key.Length + 2);
            if (colon < 0) return "";

            int firstQuote = json.IndexOf('"', colon + 1);
            if (firstQuote < 0) return "";

            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0) return "";

            return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
        }

        private static bool LooksLikeGuid(string s) {
            return Guid.TryParse(s.Trim(), out _);
        }
    }
}
