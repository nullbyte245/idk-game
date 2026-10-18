using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Minimal SVG path rasterizer for the flat single-colour Phosphor icons used by the UI.
/// Supports the M/L/H/V/C/S/Q/T/A/Z command set (absolute and relative), converts arcs to
/// cubics and fills with the non-zero winding rule, which is how the icon set is authored.
/// </summary>
public static class SvgRaster
{
    /// <summary>
    /// GDI+ anti-aliasing alone leaves visible staircases once Unity minifies the glyph to the ~38 px
    /// it is drawn at, so every icon is rastered this many times larger and box-filtered down.
    /// </summary>
    private const int Supersample = 4;

    public static void Render(string svgFile, string pngFile, int size, Color color, int padding)
    {
        var text = File.ReadAllText(svgFile);

        var viewBox = Regex.Match(text, "viewBox=\"([^\"]+)\"");
        var box = new[] { 0f, 0f, 256f, 256f };
        if (viewBox.Success)
        {
            var parts = Regex.Split(viewBox.Groups[1].Value.Trim(), "[ ,]+");
            for (var i = 0; i < 4 && i < parts.Length; i++)
                box[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
        }

        var big = size * Supersample;
        using (var path = new GraphicsPath { FillMode = FillMode.Winding })
        {
            foreach (Match data in Regex.Matches(text, "d=\"([^\"]+)\""))
                ParsePath(data.Groups[1].Value, path, box, big, padding * Supersample);

            using (var source = new Bitmap(big, big, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var graphics = Graphics.FromImage(source))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    using (var brush = new SolidBrush(color))
                        graphics.FillPath(brush, path);
                }

                using (var target = Downsample(source, size))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(pngFile));
                    target.Save(pngFile, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
    }

    /// <summary>
    /// Box filter straight over the premultiplied bytes Format32bppArgb stores, which is what makes the
    /// average correct: blending non-premultiplied RGB would fringe every partially covered edge.
    /// </summary>
    private static Bitmap Downsample(Bitmap source, int size)
    {
        var target = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        var full = new Rectangle(0, 0, source.Width, source.Height);
        var srcData = source.LockBits(full, System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var dstData = target.LockBits(new Rectangle(0, 0, size, size),
            System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            var src = new int[srcData.Stride / 4 * source.Height];
            System.Runtime.InteropServices.Marshal.Copy(srcData.Scan0, src, 0, src.Length);
            var dst = new int[size * size];

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    int b = 0, g = 0, r = 0, a = 0;
                    for (var sy = 0; sy < Supersample; sy++)
                    {
                        var row = ((y * Supersample + sy) * srcData.Stride) / 4;
                        for (var sx = 0; sx < Supersample; sx++)
                        {
                            var pixel = (uint)src[row + x * Supersample + sx];
                            b += (int)(pixel & 0xFF);
                            g += (int)((pixel >> 8) & 0xFF);
                            r += (int)((pixel >> 16) & 0xFF);
                            a += (int)((pixel >> 24) & 0xFF);
                        }
                    }

                    const int n = Supersample * Supersample;
                    dst[y * size + x] = (a / n << 24) | (r / n << 16) | (g / n << 8) | (b / n);
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(dst, 0, dstData.Scan0, dst.Length);
        }
        finally
        {
            source.UnlockBits(srcData);
            target.UnlockBits(dstData);
        }

        return target;
    }

    // ------------------------------------------------------------------ path parsing

    /// <summary>A path cursor lives in viewBox units; only points handed to GraphicsPath are projected.</summary>
    private sealed class Cursor
    {
        public float Scale, OffsetX, OffsetY;
        public PointF At(float x, float y) { return new PointF(x * Scale + OffsetX, y * Scale + OffsetY); }
    }

    private static void ParsePath(string d, GraphicsPath path, float[] box, int size, int padding)
    {
        var cursor = new Cursor
        {
            Scale = (size - 2f * padding) / Math.Max(box[2], box[3]),
        };
        cursor.OffsetX = padding + (size - 2f * padding - box[2] * cursor.Scale) * 0.5f - box[0] * cursor.Scale;
        cursor.OffsetY = padding + (size - 2f * padding - box[3] * cursor.Scale) * 0.5f - box[1] * cursor.Scale;

        float x = 0f, y = 0f;
        float startX = 0f, startY = 0f;
        float controlX = 0f, controlY = 0f;
        var command = '\0';
        var lastCommand = '\0';

        var index = 0;
        while (index < d.Length)
        {
            var c = d[index];

            if (c == ',' || c == ' ' || c == '\t' || c == '\r' || c == '\n') { index++; continue; }

            if (char.IsLetter(c))
            {
                command = c;
                index++;
                continue;
            }

            if (command == '\0') { index++; continue; }

            var current = command;
            var relative = char.IsLower(current);
            var name = char.ToUpperInvariant(current);

            switch (name)
            {
                case 'M':
                {
                    x = ReadNumber(d, ref index);
                    y = ReadNumber(d, ref index);
                    if (relative) { x += startX; y += startY; }
                    startX = x; startY = y;
                    path.StartFigure();
                    // Repeated coordinates after a moveto are implicit lineto commands.
                    command = relative ? 'l' : 'L';
                    break;
                }
                case 'L':
                {
                    var toX = ReadNumber(d, ref index);
                    var toY = ReadNumber(d, ref index);
                    if (relative) { toX += x; toY += y; }
                    AddLine(path, cursor, x, y, toX, toY);
                    x = toX; y = toY;
                    break;
                }
                case 'H':
                {
                    var toX = ReadNumber(d, ref index);
                    if (relative) toX += x;
                    AddLine(path, cursor, x, y, toX, y);
                    x = toX;
                    break;
                }
                case 'V':
                {
                    var toY = ReadNumber(d, ref index);
                    if (relative) toY += y;
                    AddLine(path, cursor, x, y, x, toY);
                    y = toY;
                    break;
                }
                case 'C':
                {
                    float c1x = ReadNumber(d, ref index), c1y = ReadNumber(d, ref index);
                    float c2x = ReadNumber(d, ref index), c2y = ReadNumber(d, ref index);
                    float toX = ReadNumber(d, ref index), toY = ReadNumber(d, ref index);
                    if (relative)
                    {
                        c1x += x; c1y += y; c2x += x; c2y += y; toX += x; toY += y;
                    }
                    path.AddBezier(cursor.At(x, y), cursor.At(c1x, c1y), cursor.At(c2x, c2y), cursor.At(toX, toY));
                    controlX = c2x; controlY = c2y;
                    x = toX; y = toY;
                    break;
                }
                case 'S':
                {
                    float c2x = ReadNumber(d, ref index), c2y = ReadNumber(d, ref index);
                    float toX = ReadNumber(d, ref index), toY = ReadNumber(d, ref index);
                    if (relative) { c2x += x; c2y += y; toX += x; toY += y; }

                    var reflected = Reflect(controlX, controlY, x, y, WasCurve(lastCommand));
                    path.AddBezier(cursor.At(x, y), cursor.At(reflected.Item1, reflected.Item2),
                        cursor.At(c2x, c2y), cursor.At(toX, toY));
                    controlX = c2x; controlY = c2y;
                    x = toX; y = toY;
                    break;
                }
                case 'Q':
                {
                    float qx = ReadNumber(d, ref index), qy = ReadNumber(d, ref index);
                    float toX = ReadNumber(d, ref index), toY = ReadNumber(d, ref index);
                    if (relative) { qx += x; qy += y; toX += x; toY += y; }
                    AddQuadratic(path, cursor, x, y, qx, qy, toX, toY);
                    controlX = qx; controlY = qy;
                    x = toX; y = toY;
                    break;
                }
                case 'T':
                {
                    float toX = ReadNumber(d, ref index), toY = ReadNumber(d, ref index);
                    if (relative) { toX += x; toY += y; }
                    var reflected = Reflect(controlX, controlY, x, y, WasCurve(lastCommand));
                    AddQuadratic(path, cursor, x, y, reflected.Item1, reflected.Item2, toX, toY);
                    controlX = reflected.Item1; controlY = reflected.Item2;
                    x = toX; y = toY;
                    break;
                }
                case 'A':
                {
                    float rx = ReadNumber(d, ref index), ry = ReadNumber(d, ref index);
                    var rotation = ReadNumber(d, ref index);
                    var largeArc = ReadNumber(d, ref index) >= 0.5f;
                    var sweep = ReadNumber(d, ref index) >= 0.5f;
                    float toX = ReadNumber(d, ref index), toY = ReadNumber(d, ref index);
                    if (relative) { toX += x; toY += y; }
                    AddArc(path, cursor, x, y, toX, toY, rx, ry, rotation, largeArc, sweep);
                    x = toX; y = toY;
                    break;
                }
                case 'Z':
                {
                    AddLine(path, cursor, x, y, startX, startY);
                    path.CloseFigure();
                    x = startX; y = startY;
                    break;
                }
                default:
                    index++;
                    continue;
            }

            lastCommand = current;
        }
    }

    private static bool WasCurve(char command)
    {
        var name = char.ToUpperInvariant(command);
        return name == 'C' || name == 'S' || name == 'Q' || name == 'T';
    }

    private static Tuple<float, float> Reflect(float controlX, float controlY, float x, float y, bool reflect)
    {
        if (!reflect) return Tuple.Create(x, y);
        return Tuple.Create(2f * x - controlX, 2f * y - controlY);
    }

    private static void AddLine(GraphicsPath path, Cursor cursor, float fromX, float fromY, float toX, float toY)
    {
        if (Math.Abs(fromX - toX) < 0.0001f && Math.Abs(fromY - toY) < 0.0001f) return;
        path.AddLine(cursor.At(fromX, fromY), cursor.At(toX, toY));
    }

    private static void AddQuadratic(GraphicsPath path, Cursor cursor, float fromX, float fromY,
        float controlX, float controlY, float toX, float toY)
    {
        var c1x = fromX + (controlX - fromX) * (2f / 3f);
        var c1y = fromY + (controlY - fromY) * (2f / 3f);
        var c2x = toX + (controlX - toX) * (2f / 3f);
        var c2y = toY + (controlY - toY) * (2f / 3f);
        path.AddBezier(cursor.At(fromX, fromY), cursor.At(c1x, c1y), cursor.At(c2x, c2y), cursor.At(toX, toY));
    }

    // ------------------------------------------------------------------ arcs

    private static void AddArc(GraphicsPath path, Cursor cursor, float fromX, float fromY, float toX, float toY,
        float rx, float ry, float rotationDegrees, bool largeArc, bool sweepFlag)
    {
        if (Math.Abs(rx) < 0.0001f || Math.Abs(ry) < 0.0001f)
        {
            AddLine(path, cursor, fromX, fromY, toX, toY);
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);

        var phi = rotationDegrees * (float)Math.PI / 180f;
        var cosPhi = (float)Math.Cos(phi);
        var sinPhi = (float)Math.Sin(phi);

        var halfDx = (fromX - toX) / 2f;
        var halfDy = (fromY - toY) / 2f;

        var x1 = cosPhi * halfDx + sinPhi * halfDy;
        var y1 = -sinPhi * halfDx + cosPhi * halfDy;

        var lambda = (x1 * x1) / (rx * rx) + (y1 * y1) / (ry * ry);
        if (lambda > 1f)
        {
            var root = (float)Math.Sqrt(lambda);
            rx *= root;
            ry *= root;
        }

        var numerator = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
        var denominator = rx * rx * y1 * y1 + ry * ry * x1 * x1;
        var factor = denominator <= 0f ? 0f : (float)Math.Sqrt(Math.Max(0f, numerator / denominator));
        if (largeArc == sweepFlag) factor = -factor;

        var centreX = factor * rx * y1 / ry;
        var centreY = factor * -ry * x1 / rx;

        var ellipseCentreX = cosPhi * centreX - sinPhi * centreY + (fromX + toX) / 2f;
        var ellipseCentreY = sinPhi * centreX + cosPhi * centreY + (fromY + toY) / 2f;

        var startAngle = AngleBetween(1f, 0f, (x1 - centreX) / rx, (y1 - centreY) / ry);
        var delta = AngleBetween((x1 - centreX) / rx, (y1 - centreY) / ry,
            (-x1 - centreX) / rx, (-y1 - centreY) / ry);

        if (!sweepFlag && delta > 0) delta -= 2f * (float)Math.PI;
        else if (sweepFlag && delta < 0) delta += 2f * (float)Math.PI;

        // A cubic tracks an elliptical arc closely enough below a quarter turn.
        var segments = (int)Math.Ceiling(Math.Abs(delta) / ((float)Math.PI / 2f));
        if (segments < 1) segments = 1;

        var segmentAngle = delta / segments;
        var halfSegment = segmentAngle / 2f;
        var alpha = (float)Math.Sin(segmentAngle) *
                    ((float)Math.Sqrt(4 + 3 * Math.Pow(Math.Tan(halfSegment), 2)) - 1f) / 3f;

        var angle = startAngle;
        var point = PointOnEllipse(ellipseCentreX, ellipseCentreY, rx, ry, cosPhi, sinPhi, angle);
        var tangent = Tangent(rx, ry, cosPhi, sinPhi, angle);

        for (var i = 0; i < segments; i++)
        {
            var nextAngle = angle + segmentAngle;
            var nextPoint = PointOnEllipse(ellipseCentreX, ellipseCentreY, rx, ry, cosPhi, sinPhi, nextAngle);
            var nextTangent = Tangent(rx, ry, cosPhi, sinPhi, nextAngle);

            path.AddBezier(cursor.At(point.X, point.Y),
                cursor.At(point.X + alpha * tangent.X, point.Y + alpha * tangent.Y),
                cursor.At(nextPoint.X - alpha * nextTangent.X, nextPoint.Y - alpha * nextTangent.Y),
                cursor.At(nextPoint.X, nextPoint.Y));

            point = nextPoint;
            tangent = nextTangent;
            angle = nextAngle;
        }
    }

    private static float AngleBetween(float ux, float uy, float vx, float vy)
    {
        var dot = ux * vx + uy * vy;
        var length = (float)Math.Sqrt(ux * ux + uy * uy) * (float)Math.Sqrt(vx * vx + vy * vy);
        if (length <= 0f) return 0f;

        var angle = (float)Math.Acos(Math.Max(-1f, Math.Min(1f, dot / length)));
        return ux * vy - uy * vx < 0 ? -angle : angle;
    }

    private static PointF PointOnEllipse(float centreX, float centreY, float rx, float ry,
        float cosPhi, float sinPhi, float t)
    {
        var x = rx * (float)Math.Cos(t);
        var y = ry * (float)Math.Sin(t);
        return new PointF(cosPhi * x - sinPhi * y + centreX, sinPhi * x + cosPhi * y + centreY);
    }

    private static PointF Tangent(float rx, float ry, float cosPhi, float sinPhi, float t)
    {
        var dx = -rx * (float)Math.Sin(t);
        var dy = ry * (float)Math.Cos(t);
        return new PointF(cosPhi * dx - sinPhi * dy, sinPhi * dx + cosPhi * dy);
    }

    // ------------------------------------------------------------------ numbers

    private static float ReadNumber(string d, ref int index)
    {
        while (index < d.Length && (d[index] == ',' || d[index] == ' ' || d[index] == '\t' ||
                                    d[index] == '\r' || d[index] == '\n'))
            index++;

        var start = index;
        if (index < d.Length && (d[index] == '-' || d[index] == '+')) index++;

        var seenDot = false;
        var seenExponent = false;
        while (index < d.Length)
        {
            var c = d[index];
            if (char.IsDigit(c)) { index++; continue; }
            if (c == '.' && !seenDot && !seenExponent) { seenDot = true; index++; continue; }
            if ((c == 'e' || c == 'E') && !seenExponent && index > start)
            {
                seenExponent = true;
                index++;
                if (index < d.Length && (d[index] == '-' || d[index] == '+')) index++;
                continue;
            }
            break;
        }

        if (index == start)
        {
            // Malformed input: skip a character rather than spinning forever.
            index++;
            return 0f;
        }

        return float.Parse(d.Substring(start, index - start), CultureInfo.InvariantCulture);
    }
}
