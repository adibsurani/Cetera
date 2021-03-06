using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Cetera.IO;

namespace Cetera.Image
{
    public enum Format : byte
    {
        RGBA8888, RGB888,
        RGBA5551, RGB565, RGBA4444,
        LA88, HL88, L8, A8, LA44,
        L4, A4, ETC1, ETC1A4
    }

    public enum Orientation : byte
    {
        Default,
        TransposeTile = 1,
        Rotate90 = 4,
        Transpose = 8
    }

    public class ImageSettings
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public Format Format { get; set; }
        public Orientation Orientation { get; set; } = Orientation.Default;
        public bool PadToPowerOf2 { get; set; } = true;

        /// <summary>
        /// This is currently a hack
        /// </summary>
        public void SetFormat<T>(T originalFormat) where T : struct, IConvertible
        {
            Format = ConvertFormat(originalFormat);
        }

        public static Format ConvertFormat<T>(T originalFormat) where T : struct, IConvertible
        {
            return (Format)Enum.Parse(typeof(Format), originalFormat.ToString());
        }
    }

    public class Common
    {
        static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max - 1);

        static IEnumerable<Color> GetColorsFromTexture(byte[] tex, Format format)
        {
            using (var br = new BinaryReaderX(new MemoryStream(tex)))
            {
                var etc1decoder = new ETC1.Decoder();

                while (true)
                {
                    int a = 255, r = 255, g = 255, b = 255;
                    switch (format)
                    {
                        case Format.L8:
                            b = g = r = br.ReadByte();
                            break;
                        case Format.A8:
                            a = br.ReadByte();
                            break;
                        case Format.LA44:
                            a = br.ReadNibble() * 17;
                            b = g = r = br.ReadNibble() * 17;
                            break;
                        case Format.LA88:
                            a = br.ReadByte();
                            b = g = r = br.ReadByte();
                            break;
                        case Format.HL88:
                            g = br.ReadByte();
                            r = br.ReadByte();
                            break;
                        case Format.RGB565:
                            var s = br.ReadUInt16();
                            b = (s % 32) * 33 / 4;
                            g = (s >> 5) % 64 * 65 / 16;
                            r = (s >> 11) * 33 / 4;
                            break;
                        case Format.RGB888:
                            b = br.ReadByte();
                            g = br.ReadByte();
                            r = br.ReadByte();
                            break;
                        case Format.RGBA5551:
                            var s2 = br.ReadUInt16();
                            a = (s2 & 1) * 255;
                            b = (s2 >> 1) % 32 * 33 / 4;
                            g = (s2 >> 6) % 32 * 33 / 4;
                            r = (s2 >> 11) % 32 * 33 / 4;
                            break;
                        case Format.RGBA4444:
                            a = br.ReadNibble() * 17;
                            b = br.ReadNibble() * 17;
                            g = br.ReadNibble() * 17;
                            r = br.ReadNibble() * 17;
                            break;
                        case Format.RGBA8888:
                            a = br.ReadByte();
                            b = br.ReadByte();
                            g = br.ReadByte();
                            r = br.ReadByte();
                            break;
                        case Format.ETC1:
                        case Format.ETC1A4:
                            yield return etc1decoder.Get(() =>
                            {
                                var alpha = (format == Format.ETC1A4) ? br.ReadUInt64() : ulong.MaxValue;
                                return new ETC1.PixelData { Alpha = alpha, Block = br.ReadStruct<ETC1.Block>() };
                            });
                            continue;
                        case Format.L4:
                            b = g = r = br.ReadNibble() * 17;
                            break;
                        case Format.A4:
                            a = br.ReadNibble() * 17;
                            break;
                        default:
                            throw new NotSupportedException($"Unknown image format {format}");
                    }
                    yield return Color.FromArgb(a, r, g, b);
                }
            }
        }

        static IEnumerable<Point> GetPointSequence(ImageSettings settings)
        {
            int strideWidth = (settings.Width + 7) & ~7;
            int strideHeight = (settings.Height + 7) & ~7;
            if (settings.PadToPowerOf2)
            {
                strideWidth = 2 << (int)Math.Log(strideWidth - 1, 2);
                strideHeight = 2 << (int)Math.Log(strideHeight - 1, 2);
            }
            int stride = (int)settings.Orientation < 4 ? strideWidth : strideHeight;
            for (int i = 0; i < strideWidth * strideHeight; i++)
            {
                int x_out = (i / 64 % (stride / 8)) * 8;
                int y_out = (i / 64 / (stride / 8)) * 8;
                int x_in = (i / 4 & 4) | (i / 2 & 2) | (i & 1);
                int y_in = (i / 8 & 4) | (i / 4 & 2) | (i / 2 & 1);

                switch (settings.Orientation)
                {
                    case Orientation.Default:
                        yield return new Point(x_out + x_in, y_out + y_in);
                        break;
                    case Orientation.TransposeTile:
                        yield return new Point(x_out + y_in, y_out + x_in);
                        break;
                    case Orientation.Rotate90:
                        yield return new Point(y_out + y_in, stride - 1 - (x_out + x_in));
                        break;
                    case Orientation.Transpose:
                        yield return new Point(y_out + y_in, x_out + x_in);
                        break;
                    default:
                        throw new NotSupportedException($"Unknown orientation format {settings.Orientation}");
                }
            }
        }

        public static Bitmap Load(byte[] tex, ImageSettings settings)
        {
            int width = settings.Width, height = settings.Height;
            var colors = GetColorsFromTexture(tex, settings.Format);
            var points = GetPointSequence(settings);

            // Now we just need to merge the points with the colors
            var bmp = new Bitmap(width, height);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            unsafe
            {
                var ptr = (int*)data.Scan0;
                foreach (var pair in points.Zip(colors, Tuple.Create))
                {
                    int x = pair.Item1.X, y = pair.Item1.Y;
                    if (0 <= x && x < width && 0 <= y && y < height)
                    {
                        ptr[data.Stride * y / 4 + x] = pair.Item2.ToArgb();
                    }
                }
            }
            bmp.UnlockBits(data);
            return bmp;
        }

        public static byte[] Save(Bitmap bmp, ImageSettings settings)
        {
            settings.Width = bmp.Width;
            settings.Height = bmp.Height;
            var points = GetPointSequence(settings);

            var ms = new MemoryStream();
            var etc1encoder = new ETC1.Encoder();

            using (var bw = new BinaryWriterX(ms))
            {
                foreach (var point in points)
                {
                    int x = Clamp(point.X, 0, bmp.Width);
                    int y = Clamp(point.Y, 0, bmp.Height);

                    var color = bmp.GetPixel(x, y);
                    //if (color.A == 0) color = default(Color); // daigasso seems to need this

                    switch (settings.Format)
                    {
                        case Format.L8:
                            bw.Write(color.G);
                            break;
                        case Format.A8:
                            bw.Write(color.A);
                            break;
                        case Format.LA44:
                            bw.WriteNibble(color.A / 16);
                            bw.WriteNibble(color.G / 16);
                            break;
                        case Format.LA88:
                            bw.Write(color.A);
                            bw.Write(color.G);
                            break;
                        case Format.HL88:
                            bw.Write(color.G);
                            bw.Write(color.R);
                            break;
                        case Format.RGB565:
                            bw.Write((short)((color.R / 8 << 11) | (color.G / 4 << 5) | (color.B / 8)));
                            break;
                        case Format.RGB888:
                            bw.Write(color.B);
                            bw.Write(color.G);
                            bw.Write(color.R);
                            break;
                        case Format.RGBA5551:
                            bw.Write((short)((color.R / 8 << 11) | (color.G / 8 << 6) | (color.B / 8 << 1) | color.A / 128));
                            break;
                        case Format.RGBA4444:
                            bw.WriteNibble(color.A / 16);
                            bw.WriteNibble(color.B / 16);
                            bw.WriteNibble(color.G / 16);
                            bw.WriteNibble(color.R / 16);
                            break;
                        case Format.RGBA8888:
                            bw.Write(color.A);
                            bw.Write(color.B);
                            bw.Write(color.G);
                            bw.Write(color.R);
                            break;
                        case Format.ETC1:
                        case Format.ETC1A4:
                            etc1encoder.Set(color, data =>
                            {
                                if (settings.Format == Format.ETC1A4) bw.Write(data.Alpha);
                                bw.WriteStruct(data.Block);
                            });
                            break;
                        case Format.L4:
                            bw.WriteNibble(color.G / 16);
                            break;
                        case Format.A4:
                            bw.WriteNibble(color.A / 16);
                            break;
                        default:
                            throw new NotSupportedException();
                    }
                }
            }

            return ms.ToArray();
        }
    }
}
