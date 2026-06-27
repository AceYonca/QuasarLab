# QuasarLab

QuasarLab is a Windows protocol workbench for studying Quasar-compatible lab traffic in a controlled environment. It provides profile-driven TLS connections, protobuf message framing, connection monitoring, message logs, and packet inspection from a focused WPF desktop UI.

> Built for authorized research, reverse engineering, QA, and defensive lab validation. Run it only in environments you own or have explicit permission to test.

## Highlights

- WPF dashboard for connection state, server reachability, and lab activity.
- Debug and release-style client profiles with editable host, port, identity, tag, key, and signature fields.
- TLS 1.2 transport with protobuf-net serialization for Quasar-compatible message framing.
- Controlled connection spawning and auto-reconnect behavior for repeatable protocol testing.
- Message log with copy and clear actions for clean analysis workflows.
- Packet inspector for inbound and outbound frames, payload sizes, message types, notes, and hex output.
- JSON profile import for quickly loading release-style profile values.

## Use Cases

- Inspecting Quasar-compatible client identification and message framing.
- Validating private protocol research against a controlled listener or test harness.
- Reproducing connection and reconnect behavior during defensive lab work.
- Teaching or documenting TLS-wrapped protobuf protocol flows.
- Building repeatable packet traces for analysis notes and regression checks.

## Scope

QuasarLab is intentionally scoped as a lab and inspection tool. It does not include persistence, credential access, payload deployment, lateral movement, or remote-control modules. Keep usage limited to isolated systems and authorized research networks.

## Requirements

- Windows
- Visual Studio 2022
- .NET Framework 4.8.1 Developer Pack
- NuGet packages listed in `QuasarLab/packages.config`

## Build

Open `QuasarLab.sln` in Visual Studio and build the solution, or build from a Visual Studio Developer PowerShell:

```powershell
MSBuild.exe QuasarLab\QuasarLab.csproj /p:Configuration=Release
```

The compiled application is written to:

```text
QuasarLab\bin\Release\QuasarLab.exe
```

## Project Layout

```text
QuasarLab/
  QuasarLab.sln
  QuasarLab/
    MainWindow.xaml              WPF interface
    MainWindow.xaml.cs           UI workflow and view updates
    Core/
      ClientProfile.cs           Debug/release lab profile model
      QuasarLabService.cs        Connection lifecycle and packet capture events
    QuasarCLI/
      Networking/                TLS connection and packet tracing
      Protocol/                  Framing, payload readers/writers, message registry
      Protocol/Messages/         Protobuf message contracts
```

## GitHub About

Suggested repository description:

```text
Windows WPF protocol workbench for Quasar-compatible lab traffic: profile-driven TLS connections, protobuf framing, message logs, and packet inspection for authorized research.
```

Suggested topics:

```text
csharp, wpf, windows, dotnet-framework, protobuf-net, tls, packet-inspection, protocol-analysis, reverse-engineering, malware-analysis, security-research, lab-tooling
```

## Responsible Use

This project is for legitimate security research, education, and internal testing. Do not use it against systems, services, or networks without clear authorization.

For disclosure guidance and contribution boundaries, see `SECURITY.md`.
