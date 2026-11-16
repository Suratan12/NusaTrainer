using System.Globalization;
using NusaTrainer.App.Services;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Trainer;
using NusaTrainer.Core.Util;

namespace NusaTrainer.App.ViewModels;

public sealed class CheatViewModel : ViewModelBase
{
    private TrainerSession? _session;
    private bool _isEnabled;
    private double _sliderValue;
    private string? _errorText;
    private bool _isBusy;
    private bool _suppress;

    // Penulisan slider di-coalesce: hanya nilai terakhir yang ditulis.
    private double? _pendingSlider;
    private bool _sliderWorker;

    public CheatDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Category => Definition.Category;
    public string? Description => Definition.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Definition.Description);
    public bool ShowCategoryHeader { get; init; }

    public bool IsToggle => Definition.Type == CheatKind.Toggle;
    public bool IsSlider => Definition.Type == CheatKind.Slider;
    public bool IsButton => Definition.Type == CheatKind.Button;
    public bool IsLockable => IsSlider && Definition.Operation == CheatOperation.Freeze;

    public double Min => Definition.Min;
    public double Max => Definition.Max;
    public double Step => Definition.Step;

    public IReadOnlyList<string> Hotkeys { get; }
    public bool HasHotkeys => Hotkeys.Count > 0;

    public RelayCommand PressCommand { get; }

    public CheatViewModel(CheatDefinition definition)
    {
        Definition = definition;
        _sliderValue = definition.Default;
        var keys = new List<string>();
        if (definition.Hotkey is { } h) keys.Add(HotkeyParser.Pretty(h));
        if (definition.HotkeyDecrease is { } d) keys.Add("− " + HotkeyParser.Pretty(d));
        if (definition.HotkeyIncrease is { } i) keys.Add("+ " + HotkeyParser.Pretty(i));
        Hotkeys = keys;
        PressCommand = new RelayCommand(Press, () => CanInteract);
    }

    public bool CanInteract => _session is not null && !_isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) { OnPropertyChanged(nameof(CanInteract)); PressCommand.RaiseCanExecuteChanged(); }
        }
    }

    public string? ErrorText
    {
        get => _errorText;
        set { if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!Set(ref _isEnabled, value) || _suppress) return;
            _ = ApplyToggleAsync(value);
        }
    }

    public double SliderValue
    {
        get => _sliderValue;
        set
        {
            value = Math.Round(Math.Clamp(value, Min, Max) / Step) * Step;
            if (!Set(ref _sliderValue, value)) return;
            OnPropertyChanged(nameof(SliderValueText));
            if (_session is not null) QueueSliderWrite(value);
        }
    }

    public string SliderValueText =>
        Step < 1 ? _sliderValue.ToString("0.0#", CultureInfo.CurrentCulture) : _sliderValue.ToString("N0", CultureInfo.CurrentCulture);

    public void Attach(TrainerSession? session)
    {
        _session = session;
        ErrorText = null;
        if (session is null) SetEnabledSilently(false);
        OnPropertyChanged(nameof(CanInteract));
        PressCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Dipanggil bila sesi mematikan cheat sendiri (mis. pointer hilang).</summary>
    public void OnFailed(string message)
    {
        SetEnabledSilently(false);
        ErrorText = message;
    }

    public void ToggleFromHotkey()
    {
        if (!CanInteract) return;
        if (IsButton) Press();
        else if (IsToggle || IsLockable) IsEnabled = !IsEnabled;
    }

    public void StepFromHotkey(int direction)
    {
        if (!CanInteract) return;
        SliderValue += direction * Step;
        if (IsLockable && !IsEnabled) IsEnabled = true;
    }

    private void SetEnabledSilently(bool value)
    {
        _suppress = true;
        IsEnabled = value;
        _suppress = false;
    }

    private async Task ApplyToggleAsync(bool value)
    {
        var session = _session;
        if (session is null) { SetEnabledSilently(false); return; }
        IsBusy = true;
        ErrorText = null;
        try
        {
            await Task.Run(() =>
            {
                if (value && IsSlider) session.SetSliderValue(Definition.Id, _sliderValue);
                session.SetToggle(Definition.Id, value);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Cheat {Definition.Id}: {ex.Message}");
            SetEnabledSilently(!value);
            ErrorText = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private void Press()
    {
        var session = _session;
        if (session is null) return;
        IsBusy = true;
        ErrorText = null;
        Task.Run(() => session.Press(Definition.Id)).ContinueWith(t => OnUi(() =>
        {
            IsBusy = false;
            if (t.Exception is { } ex) ErrorText = ex.InnerException?.Message ?? ex.Message;
        }));
    }

    private void QueueSliderWrite(double value)
    {
        _pendingSlider = value;
        if (_sliderWorker) return;
        _sliderWorker = true;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                double? v = null;
                await Dispatcher(() => { v = _pendingSlider; _pendingSlider = null; if (v is null) _sliderWorker = false; });
                if (v is null) return;
                try
                {
                    _session?.SetSliderValue(Definition.Id, v.Value);
                    OnUi(() => ErrorText = null);
                }
                catch (Exception ex) { OnUi(() => ErrorText = ex.Message); }
            }
        });
    }

    private static Task Dispatcher(Action a) => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(a).GetTask();
}
