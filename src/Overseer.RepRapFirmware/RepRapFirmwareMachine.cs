using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

[MachineType("RepRapFirmware (Standalone)")]
public record RepRapFirmwareMachine : Machine
{
  public RepRapFirmwareMachine() { }

  public RepRapFirmwareMachine(Machine machine)
  {
    if (machine is not null)
    {
      MachineType = machine.MachineType;
      Id = machine.Id;
      Name = machine.Name;
      Disabled = machine.Disabled;
      WebcamUrl = machine.WebcamUrl;
      WebcamOrientation = machine.WebcamOrientation;
      Tools = machine.Tools;
      SortIndex = machine.SortIndex;
      Properties = machine.Properties;
    }
  }

  public const string DefaultPassword = "reprap";

  [MachineProperty(displayName: "URL", description: "The URL of the machine's web interface")]
  public string? Url
  {
    get => GetProperty<string?>(nameof(Url));
    set => SetProperty(nameof(Url), value);
  }

  [MachineProperty(
    displayName: "Password",
    description: "The password to use when connecting to the machine. Leave blank to use the default password.",
    isSensitive: true
  )]
  public string? Password
  {
    get => GetProperty<string?>(nameof(Password));
    set => SetProperty(nameof(Password), value);
  }
}
