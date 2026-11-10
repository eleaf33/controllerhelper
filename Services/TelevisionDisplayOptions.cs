using ControllerHelper.Models;

namespace ControllerHelper.Services;

public sealed record TelevisionDisplayCatalog(TelevisionVideoMode[] Modes, TelevisionVideoMode? CurrentMode,
    DisplayScaleRange? Scale, string? ScaleWarning, string? ModeWarning = null);

internal interface ITelevisionDisplayApi
{
    string? ScaleBlockReason { get; }
    TelevisionVideoMode[] GetModes(string televisionId);
    TelevisionVideoMode GetCurrentMode(string televisionId);
    void SetMode(string televisionId, TelevisionVideoMode mode);
    DisplayScaleRange GetScale(string televisionId);
    void SetScale(string televisionId, int relative);
}

internal sealed class TelevisionDisplayOptions(ITelevisionDisplayApi api)
{
    internal static void Validate(AppSettings settings)
    {
        settings.TelevisionMode?.Validate();
        if (settings.TelevisionScalePercent is int percent && !DisplayScaleRange.IsKnownPercent(percent))
            throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("TelevisionDisplayOptions_180"));
    }

    internal void Preflight(AppSettings settings)
    {
        Validate(settings);
        if (settings.TelevisionScalePercent is not null && api.ScaleBlockReason is string reason)
            throw new InvalidOperationException(reason);
    }

    // Register rollback before any DPI-related mutation; the caller also owns
    // a full topology/audio snapshot and must restore it even if DPI restore fails.
    internal void Apply(AppSettings settings, Action<Action> registerRollback, CancellationToken cancellation)
    {
        Preflight(settings);
        string id = settings.TelevisionId ?? throw new InvalidOperationException(ControllerHelper.Localization.Text.Get("DisplayProfileCodec_058"));
        cancellation.ThrowIfCancellationRequested();
        bool modeMayHaveChanged = false, scaleMayHaveChanged = false;
        if (settings.TelevisionScalePercent is not null)
        {
            DisplayScaleRange before = api.GetScale(id);
            before.Validate();
            registerRollback(() =>
            {
                if (modeMayHaveChanged || scaleMayHaveChanged) api.SetScale(id, before.CurrentRelative);
            });
            if (settings.TelevisionMode is not null)
            {
                // Restore the old mode first so its relative DPI range is valid
                // again, before the outer rollback deactivates the television.
                TelevisionVideoMode beforeMode = api.GetCurrentMode(id);
                registerRollback(() =>
                {
                    if (modeMayHaveChanged) api.SetMode(id, beforeMode);
                });
            }
        }
        if (settings.TelevisionMode is { } mode)
        {
            if (!api.GetModes(id).Contains(mode))
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Format("TelevisionDisplayOptions_181", mode));
            cancellation.ThrowIfCancellationRequested();
            if (api.GetCurrentMode(id) != mode)
            {
                modeMayHaveChanged = true;
                api.SetMode(id, mode);
            }
            cancellation.ThrowIfCancellationRequested();
            if (api.GetCurrentMode(id) != mode)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Format("TelevisionDisplayOptions_182", mode));
        }
        if (settings.TelevisionScalePercent is int percent)
        {
            cancellation.ThrowIfCancellationRequested();
            DisplayScaleRange current = api.GetScale(id); // Limits can change with resolution.
            int relative = current.RelativeFor(percent);
            if (current.CurrentRelative != relative)
            {
                scaleMayHaveChanged = true;
                api.SetScale(id, relative);
            }
            cancellation.ThrowIfCancellationRequested();
            if (api.GetScale(id).CurrentPercent != percent)
                throw new InvalidOperationException(ControllerHelper.Localization.Text.Format("TelevisionDisplayOptions_183", percent));
        }
    }
}

internal sealed class DisplaySettingsRollback
{
    private readonly List<Action> _actions = [];
    internal void Add(Action action) => _actions.Add(action);

    internal void Restore(Action restoreTopology)
    {
        List<Exception> errors = [];
        foreach (Action action in _actions.AsEnumerable().Reverse())
            try { action(); } catch (Exception error) { errors.Add(error); }
        try { restoreTopology(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0)
            throw new AggregateException(ControllerHelper.Localization.Text.Get("TelevisionDisplayOptions_184"), errors);
    }
}
