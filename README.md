# overseer.reprapfirmware

RepRapFirmware integration plugin for Overseer - A machine provider that enables monitoring and control of RepRapFirmware-based 3D printers.

## Features

- Real-time machine status monitoring
- Temperature monitoring for heaters and chambers
- Job control (pause, resume, cancel)
- Progress tracking and time estimation
- Support for multiple tools and extruders
- Client certificate authentication support

## Installation

This plugin is distributed as a NuGet package and can be installed in your Overseer instance.

```bash
dotnet add package Overseer.Server.RepRapFirmware
```

## Usage

Register the provider in your service configuration:

```csharp
services.AddRepRapFirmwareProvider();
```

## Configuration

The provider requires the following machine properties:

- **Url**: The base URL of the RepRapFirmware web interface (e.g., `http://192.168.1.100`)
- **Password**: Optional password for authentication (defaults to "reprap")
- **ClientCertificate**: Optional client certificate name for SSL authentication

## Supported Machines

This provider supports any 3D printer running RepRapFirmware with the HTTP API enabled, including:

- Duet 2 boards
- Duet 3 boards (in standalone mode)
- Other RepRapFirmware-compatible controllers

## License

This project is licensed under the MIT License - see the LICENSE file for details.

