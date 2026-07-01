# ProtoFlux (no-mod) version

An alternative to the C# mod: a ProtoFlux graph that uses Resonite's built-in
`WebsocketClient` component to read your Pulsoid heart rate directly, with no
mod install required.

- `Pulsoid_Heart_Rate.resonitepackage` — import this into Resonite to get the graph.
- `Pulsoid_Heart_Rate_decoded.json` — decoded contents of the package, for inspection/diffing.

Target dynamic variable: `User/Pulsoid.HeartRate` (int).
