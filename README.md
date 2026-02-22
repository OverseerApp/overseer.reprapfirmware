# RepRapFirmware Machine Providers

## Overview

This plugin integrates [RepRapFirmware](https://www.reprapfirmware.org/) with Overseer, enabling comprehensive monitoring and control of 3D printers running RepRapFirmware.

## RepRapFirmware Provider

This provider supports any 3D printer running RepRapFirmware with the HTTP API enabled:

- Duet 2 boards (standalone mode)
- Duet 3 boards (standalone mode)
- Other RepRapFirmware-compatible controllers

## Duet Software Framework Provider

This provider supports Duet 3 boards running in Duet Software Framework (SBC) mode:

- Duet 3 with Raspberry Pi
- Duet 3 with other single-board computers running DSF

## Features

- Real-time machine status monitoring via HTTP polling
- Temperature monitoring for heaters and chambers
- Job control (pause, resume, cancel)
- Progress tracking and time estimation
- Support for multiple tools and extruders
- Password-protected access
