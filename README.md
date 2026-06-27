# QuasarLab

QuasarLab is a Windows WPF lab tool for testing Quasar-RAT servers in a controlled environment.

It helps you check if a target test server is online, connect one or more lab clients, monitor connection status, inspect packet traffic, and review message logs.

> For authorized lab testing only. Use it only with servers and networks you own or have permission to test.

## Demo

[Watch the demonstration video](https://www.youtube.com/watch?v=CHZI1-WL4Ds)

## Features

- Check if a target test server is reachable.
- Create single or multiple lab client connections.
- Monitor active, lost, and reconnected clients.
- Use editable client profiles for host, port, identity, tag, key, and signature values.
- View message logs from the app.
- Inspect inbound and outbound packet details.
- Import release-style profile data from JSON.

## Use Cases

- Testing if a Quasar-compatible server is up.
- Simulating multiple lab clients.
- Checking reconnect behavior.
- Reviewing TLS/protobuf protocol traffic.
- Light stress testing in a private lab.

## Requirements

- Windows
- Visual Studio 2022
- .NET Framework 4.8.1 Developer Pack

## Build

Open `QuasarLab.sln` in Visual Studio and build the solution.

## Responsible Use

QuasarLab is made for research, QA, and private lab testing. Do not use it on servers, systems, or networks without permission.

For disclosure guidance and contribution boundaries, see `SECURITY.md`.
