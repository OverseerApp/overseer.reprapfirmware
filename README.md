# overseer.reprapfirmware

RepRapFirmware integration plugin for Overseer - Machine providers that enable monitoring and control of RepRapFirmware-based 3D printers.

## Features

### RepRapFirmware Provider
- Real-time machine status monitoring via HTTP polling
- Temperature monitoring for heaters and chambers
- Job control (pause, resume, cancel)
- Progress tracking and time estimation
- Support for multiple tools and extruders
- Client certificate authentication support
- Password-protected access

### Duet Software Framework Provider  
- Real-time machine status monitoring via WebSocket
- Temperature monitoring for heaters and chambers
- Job control (pause, resume, cancel)
- Progress tracking and time estimation
- Support for multiple tools and extruders
- Session-based authentication

## Installation

This plugin is distributed as a NuGet package and can be installed in your Overseer instance.

```bash
dotnet add package Overseer.Server.RepRapFirmware
```

## Usage

Register the providers in your service configuration:

```csharp
services.AddRepRapFirmwareProvider();
```

This registers both the RepRapFirmware (HTTP) and Duet Software Framework (WebSocket) providers.

## Configuration

### RepRapFirmware Provider

The provider requires the following machine properties:

- **Url**: The base URL of the RepRapFirmware web interface (e.g., `http://192.168.1.100`)
- **Password**: Optional password for authentication (defaults to "reprap")
- **ClientCertificate**: Optional client certificate name for SSL authentication

### Duet Software Framework Provider

The provider requires the following machine properties:

- **Url**: The base URL of the Duet Software Framework HTTP API (e.g., `http://192.168.1.100`)
- **Password**: Optional password for authentication (defaults to "reprap")

## Supported Machines

### RepRapFirmware Provider
This provider supports any 3D printer running RepRapFirmware with the HTTP API enabled:
- Duet 2 boards (standalone mode)
- Duet 3 boards (standalone mode)
- Other RepRapFirmware-compatible controllers

### Duet Software Framework Provider
This provider supports Duet 3 boards running in Duet Software Framework (SBC) mode:
- Duet 3 with Raspberry Pi
- Duet 3 with other single-board computers running DSF

## License

This project is licensed under the MIT License - see the LICENSE file for details.

