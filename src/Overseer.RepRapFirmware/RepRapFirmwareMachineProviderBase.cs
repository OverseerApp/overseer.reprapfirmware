using Overseer.RepRapFirmware.Models;
using Overseer.Server.Integration.Machines;

namespace Overseer.RepRapFirmware;

public abstract class RepRapFirmwareMachineProviderBase<TMachine> : IMachineProvider<TMachine>
  where TMachine : RepRapFirmwareMachine, new()
{
  public event EventHandler<MachineStatusEventArgs>? StatusUpdated;

  public TMachine? Machine { get; private set; }

  public abstract void Start<TMachine1>(int interval, TMachine1 machine)
    where TMachine1 : Machine, new();

  public abstract void Stop();

  public async Task PauseJob() => await ExecuteGCode("M25");

  public async Task ResumeJob() => await ExecuteGCode("M24");

  public virtual async Task CancelJob() => await ExecuteGCode("M0");

  protected abstract Task ExecuteGCode(string command);

  protected void OnStatusUpdated(MachineStatus status)
  {
    StatusUpdated?.Invoke(this, new MachineStatusEventArgs(status));
  }

  protected static MachineState MapState(RRFMachineStatus? status, MachineState? fallback = null)
  {
    return status switch
    {
      RRFMachineStatus.Processing or RRFMachineStatus.Resuming => MachineState.Operational,
      RRFMachineStatus.Paused or RRFMachineStatus.Pausing => MachineState.Paused,
      _ => fallback ?? MachineState.Idle,
    };
  }

  protected virtual Dictionary<int, MachineTemperatureStatus> ReadTemperatures(Heat heat)
  {
    return Machine
        ?.Tools.Where(m => m.ToolType == MachineToolType.Heater)
        .Select(m =>
        {
          var heater = heat.Heaters.ElementAt(m.Index);
          return new MachineTemperatureStatus
          {
            Actual = heater.Current,
            Target = heater.Active,
            HeaterIndex = m.Index,
          };
        })
        .ToDictionary(x => x.HeaterIndex) ?? [];
  }

  public static (int timeRemaining, double progress) CalculateCompletion(ObjectModel model, IEnumerable<Extruder> extruders, GCodeFileInfo file)
  {
    if (file?.Filament?.Count > 0)
    {
      var totalFilament = file.Filament.Aggregate((product, next) => product + next);
      var totalExtruded = extruders.Select(x => x.RawPosition).Aggregate((product, next) => product + next);
      var progress = totalExtruded / totalFilament * 100d;
      return (model.Job?.TimesLeft?.Filament ?? 0, Math.Max(0d, Math.Round(progress, 1)));
    }

    if (model.Job?.TimesLeft?.Slicer != null && model.Job.TimesLeft?.Slicer > 0 && model.Job.Duration != null)
    {
      var estimatedTotal = model.Job.Duration + model.Job.TimesLeft?.Slicer;
      var progress = model.Job.Duration / (double)estimatedTotal! * 100d;
      return (model.Job?.TimesLeft?.Slicer ?? 0, Math.Max(0d, Math.Round((double)progress, 1)));
    }

    var fractionPrinted = model.Job?.FilePosition / file?.Size * 100f;
    return (model.Job?.TimesLeft?.File ?? 0, fractionPrinted ?? 0);
  }
}
