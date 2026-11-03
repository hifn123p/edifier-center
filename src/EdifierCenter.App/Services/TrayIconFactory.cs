using System.Drawing;
using System.Drawing.Drawing2D;

namespace EdifierCenter.App.Services;

public enum TrayIconState
{
    Connected,
    Muted,
    Disconnected,
    Reconnecting,
}

/// <summary>
/// 运行时绘制托盘图标（应用图标微缩版 + 状态点；静音叠加红色斜杠）——
/// docs/ui.html §02 设计。以 256px 母版矢量绘制后高质量降采样，免维护多尺寸 .ico 资产。
/// </summary>
public static class TrayIconFactory
{
    public static Icon Create(TrayIconState state, int px)
    {
        using var master = RenderMaster(state, 256);
        using var bmp = new Bitmap(px, px);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(master, new Rectangle(0, 0, px, px), 0, 0, 256, 256, GraphicsUnit.Pixel);
        }
        var hIcon = bmp.GetHicon(); // GetHicon 会拷贝位图数据，随后释放 bmp 是安全的
        return Icon.FromHandle(hIcon);
    }

    private static Bitmap RenderMaster(TrayIconState state, int px)
    {
        var bmp = new Bitmap(px, px);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var body = RoundedRect(new RectangleF(0.5f, 0.5f, px - 1f, px - 1f), px * 0.26f);
            using (var bodyPath = body)
            using (var bgBrush = new LinearGradientBrush(new RectangleF(0, 0, px, px),
                Color.FromArgb(0x37, 0x3C, 0x44), Color.FromArgb(0x14, 0x16, 0x19), 55f))
            {
                g.FillPath(bgBrush, bodyPath);
            }

            using var cone = new SolidBrush(Color.FromArgb(0xC0, 0x7A, 0x45));
            using var coneDark = new SolidBrush(Color.FromArgb(0x7E, 0x48, 0x20));
            using var ring = new Pen(Color.FromArgb(0x19, 0x1B, 0x1F), Math.Max(1f, px * 0.05f));
            using var cap = new SolidBrush(Color.FromArgb(0x1B, 0x1D, 0x21));

            // 高音单元
            var tw = px * 0.21f;
            var tweeter = new RectangleF(px * 0.5f - tw, px * 0.22f - tw, tw * 2, tw * 2);
            g.FillEllipse(cone, tweeter);
            g.DrawEllipse(ring, tweeter);
            var capT = px * 0.07f;
            g.FillEllipse(cap, px * 0.5f - capT, px * 0.22f - capT, capT * 2, capT * 2);

            // 中低音单元
            var wf = px * 0.36f;
            var woofer = new RectangleF(px * 0.5f - wf, px * 0.66f - wf, wf * 2, wf * 2);
            g.FillEllipse(cone, woofer);
            g.DrawEllipse(ring, woofer);
            var capW = px * 0.12f;
            g.FillEllipse(cap, px * 0.5f - capW, px * 0.66f - capW, capW * 2, capW * 2);
            var dot = px * 0.045f;
            g.FillEllipse(coneDark, px * 0.5f - dot, px * 0.66f - dot, dot * 2, dot * 2);

            // 状态点（右下角）
            if (state is not TrayIconState.Muted)
            {
                var color = state switch
                {
                    TrayIconState.Connected => Color.FromArgb(0x3F, 0xB9, 0x50),
                    TrayIconState.Disconnected => Color.FromArgb(0x9A, 0x9A, 0x9A),
                    _ => Color.FromArgb(0xD7, 0xA0, 0x13), // Reconnecting
                };
                var dr = px * 0.14f;
                using var dotBrush = new SolidBrush(color);
                g.FillEllipse(dotBrush, px - dr * 2 - 0.5f, px - dr * 2 - 0.5f, dr * 2, dr * 2);
            }

            // 静音斜杠
            if (state == TrayIconState.Muted)
            {
                using var slash = new Pen(Color.FromArgb(0xFF, 0x62, 0x59), Math.Max(1.5f, px * 0.09f));
                slash.StartCap = LineCap.Round;
                slash.EndCap = LineCap.Round;
                g.DrawLine(slash, px * 0.18f, px * 0.82f, px * 0.82f, px * 0.18f);
            }

            // 断开整体减淡
            if (state == TrayIconState.Disconnected)
            {
                using var veil = new SolidBrush(Color.FromArgb(120, Color.FromArgb(0x9A, 0x9A, 0x9A)));
                g.FillEllipse(veil, 0, 0, px, px);
            }
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
