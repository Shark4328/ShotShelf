param(
    [string]$SourcePath = (Join-Path $PSScriptRoot '..\src\assets\ShotShelf.png'),
    [string]$IconPath = (Join-Path $PSScriptRoot '..\src\assets\ShotShelf.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$source = @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class ShotShelfIconBuilder
{
    public static void Render(string path)
    {
        using (var bitmap = new Bitmap(1024, 1024, PixelFormat.Format32bppArgb))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.Clear(Color.Transparent);

            using (var background = Rounded(new RectangleF(54, 54, 916, 916), 190))
            using (var gradient = new LinearGradientBrush(new RectangleF(54, 54, 916, 916),
                Color.FromArgb(255, 252, 244, 232), Color.FromArgb(255, 226, 205, 178), 48f))
            {
                graphics.FillPath(gradient, background);
                using (var sheen = new LinearGradientBrush(new RectangleF(54, 54, 916, 500),
                    Color.FromArgb(105, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    graphics.FillPath(sheen, background);
                using (var edge = new Pen(Color.FromArgb(120, 139, 103, 74), 8))
                    graphics.DrawPath(edge, background);
            }

            // The back of the rope passes behind the photograph.
            using (var loop = new GraphicsPath())
            {
                loop.AddEllipse(134, 178, 756, 648);
                DrawRope(graphics, loop, 74, 50, 12);
            }

            var state = graphics.Save();
            graphics.TranslateTransform(512, 486);
            graphics.RotateTransform(-5);

            using (var shadow = Rounded(new RectangleF(-270, -196, 548, 446), 38))
            using (var shadowBrush = new SolidBrush(Color.FromArgb(52, 39, 51, 58)))
                graphics.FillPath(shadowBrush, shadow);

            using (var card = Rounded(new RectangleF(-286, -226, 572, 452), 42))
            {
                using (var paper = new SolidBrush(Color.FromArgb(255, 255, 253, 246)))
                    graphics.FillPath(paper, card);
                using (var ink = new Pen(Color.FromArgb(255, 38, 56, 65), 22))
                    graphics.DrawPath(ink, card);
            }

            using (var photo = Rounded(new RectangleF(-238, -178, 476, 306), 22))
            {
                var photoState = graphics.Save();
                graphics.SetClip(photo);
                using (var sky = new LinearGradientBrush(new RectangleF(-238, -178, 476, 306),
                    Color.FromArgb(255, 174, 214, 223), Color.FromArgb(255, 232, 242, 232), 90f))
                    graphics.FillRectangle(sky, -238, -178, 476, 306);
                using (var sun = new SolidBrush(Color.FromArgb(255, 239, 165, 76)))
                    graphics.FillEllipse(sun, 80, -116, 92, 92);
                using (var far = new SolidBrush(Color.FromArgb(255, 102, 157, 143)))
                    graphics.FillPolygon(far, new[] {
                        new PointF(-238, 128), new PointF(-116, -34), new PointF(4, 76),
                        new PointF(120, -78), new PointF(238, 128)
                    });
                using (var near = new SolidBrush(Color.FromArgb(255, 45, 97, 94)))
                    graphics.FillPolygon(near, new[] {
                        new PointF(-238, 128), new PointF(-46, 3), new PointF(64, 92),
                        new PointF(154, 20), new PointF(238, 128)
                    });
                using (var snow = new SolidBrush(Color.FromArgb(255, 241, 246, 237)))
                {
                    graphics.FillPolygon(snow, new[] {
                        new PointF(-116, -34), new PointF(-86, 8), new PointF(-108, -1),
                        new PointF(-126, 14), new PointF(-146, -1)
                    });
                    graphics.FillPolygon(snow, new[] {
                        new PointF(120, -78), new PointF(148, -36), new PointF(128, -45),
                        new PointF(108, -30), new PointF(92, -44)
                    });
                }
                graphics.Restore(photoState);
                using (var photoEdge = new Pen(Color.FromArgb(255, 38, 56, 65), 12))
                    graphics.DrawPath(photoEdge, photo);
            }

            using (var caption = new SolidBrush(Color.FromArgb(105, 124, 102, 78)))
                graphics.FillRectangle(caption, -198, 158, 242, 13);
            using (var caption = new SolidBrush(Color.FromArgb(68, 124, 102, 78)))
                graphics.FillRectangle(caption, -198, 186, 154, 11);

            graphics.Restore(state);

            // A loose overhand knot and two ends make the rope read at small sizes.
            using (var knot = new GraphicsPath())
            {
                knot.AddBezier(744, 650, 878, 626, 925, 742, 806, 790);
                knot.AddBezier(806, 790, 748, 814, 724, 742, 790, 702);
                DrawRope(graphics, knot, 74, 50, 12);
            }
            using (var tail = new GraphicsPath())
            {
                tail.AddBezier(806, 786, 874, 826, 916, 826, 936, 778);
                DrawRope(graphics, tail, 70, 46, 11);
            }
            using (var wrap = new Pen(Color.FromArgb(255, 247, 190, 139), 12))
            {
                wrap.StartCap = LineCap.Round;
                wrap.EndCap = LineCap.Round;
                graphics.DrawLine(wrap, 786, 672, 836, 697);
                graphics.DrawLine(wrap, 764, 782, 804, 752);
            }

            bitmap.Save(path, ImageFormat.Png);
        }
    }

    static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    static void DrawRope(Graphics graphics, GraphicsPath path, int outerWidth, int ropeWidth, int highlightWidth)
    {
        using (var outer = new Pen(Color.FromArgb(255, 120, 55, 32), outerWidth))
        using (var rope = new Pen(Color.FromArgb(255, 196, 101, 57), ropeWidth))
        using (var highlight = new Pen(Color.FromArgb(190, 246, 181, 126), highlightWidth))
        {
            outer.LineJoin = rope.LineJoin = highlight.LineJoin = LineJoin.Round;
            outer.StartCap = outer.EndCap = rope.StartCap = rope.EndCap = LineCap.Round;
            highlight.StartCap = highlight.EndCap = LineCap.Round;
            highlight.DashStyle = DashStyle.Dash;
            highlight.DashPattern = new[] { 1.2f, 2.2f };
            graphics.DrawPath(outer, path);
            graphics.DrawPath(rope, path);
            graphics.DrawPath(highlight, path);
        }
    }

    public static void WriteIco(string sourcePath, string iconPath, int[] sizes)
    {
        using (var source = new Bitmap(sourcePath))
        using (var output = File.Create(iconPath))
        using (var writer = new BinaryWriter(output))
        {
            var frames = new List<byte[]>();
            foreach (int size in sizes)
            {
                using (var frame = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                using (var graphics = Graphics.FromImage(frame))
                {
                    graphics.SmoothingMode = SmoothingMode.HighQuality;
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.Clear(Color.Transparent);
                    graphics.DrawImage(source, new Rectangle(0, 0, size, size));
                    frames.Add(EncodeDib(frame));
                }
            }

            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)frames.Count);
            int offset = 6 + 16 * frames.Count;
            for (int i = 0; i < frames.Count; i++)
            {
                int size = sizes[i];
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frames[i].Length);
                writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
    }

    static byte[] EncodeDib(Bitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        int maskStride = ((width + 31) / 32) * 4;
        byte[] result = new byte[40 + width * height * 4 + maskStride * height];
        using (var stream = new MemoryStream(result, true))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(40);
            writer.Write(width);
            writer.Write(height * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(width * height * 4);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);

            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] row = new byte[width * 4];
                for (int y = height - 1; y >= 0; y--)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    writer.Write(row);
                }
            }
            finally { bitmap.UnlockBits(data); }
            writer.Write(new byte[maskStride * height]);
        }
        return result;
    }
}
'@

Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing
[ShotShelfIconBuilder]::Render($SourcePath)
[ShotShelfIconBuilder]::WriteIco($SourcePath, $IconPath, [int[]](16, 24, 32, 48, 64, 128, 256))
Write-Output ('Icon source: ' + $SourcePath)
Write-Output ('Icon file:   ' + $IconPath)
