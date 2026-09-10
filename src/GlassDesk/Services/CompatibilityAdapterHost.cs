using GlassDesk.Models;

namespace GlassDesk.Services;

public interface ICompatibilityAdapter
{
    string Id { get; }
    bool CanHandle(WindowCandidate target);
    GlassDeskResult TryApply(WindowCandidate target, byte opacityPercent);
}

public sealed class CompatibilityAdapterHost
{
    private readonly IReadOnlyList<ICompatibilityAdapter> _adapters;

    public CompatibilityAdapterHost(IEnumerable<ICompatibilityAdapter>? adapters = null)
    {
        _adapters = adapters?.ToArray() ?? Array.Empty<ICompatibilityAdapter>();
    }

    public GlassDeskResult TryApply(WindowCandidate target, byte opacityPercent)
    {
        var adapter = _adapters.FirstOrDefault(item => item.CanHandle(target));
        return adapter is null
            ? new(GlassDeskStatus.Unsupported, "没有针对该应用版本的兼容适配器。")
            : adapter.TryApply(target, opacityPercent);
    }
}
