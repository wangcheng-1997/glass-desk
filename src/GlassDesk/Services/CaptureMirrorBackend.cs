using GlassDesk.Models;

namespace GlassDesk.Services;

/// <summary>
/// Stage-two boundary for Windows.Graphics.Capture + D3D11.
/// The MVP intentionally does not claim mirror support until source-window
/// hiding, frame continuity, and restoration are proven by a native prototype.
/// </summary>
public sealed class CaptureMirrorBackend
{
    public GlassDeskResult StartReadOnlyMirror(WindowCandidate target)
    {
        if (target.IsProtectedContent)
        {
            return new(GlassDeskStatus.Unsupported, "目标窗口声明了受保护内容，不能进入镜像模式。");
        }

        return new(
            GlassDeskStatus.MirrorUnavailable,
            "只读镜像尚未启用：必须先完成 Windows.Graphics.Capture 源窗口隐藏、帧持续性和恢复验证。");
    }
}
