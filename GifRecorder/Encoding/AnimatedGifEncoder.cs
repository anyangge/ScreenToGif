using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using ScreenToGif.Util;

namespace ScreenToGif.Encoding
{
    /// <summary>
    /// Animated Gif Encoder Class
    /// </summary>
    public class AnimatedGifEncoder : IDisposable
    {
        #region Variables

        /// <summary>
        /// Image width.
        /// </summary>
        private int _width;

        /// <summary>
        /// Image height.
        /// </summary>
        private int _height;

        /// <summary>
        /// Transparent color if given.
        /// </summary>
        private Color _transparent = Color.Empty;

        /// <summary>
        /// Transparent index in color table.
        /// </summary>
        private int _transIndex;

        /// <summary>
        /// The number of interations, default as "no repeat".
        /// </summary>
        private int _repeat = -1;

        /// <summary>
        /// Frame delay.
        /// </summary>
        private int _delay = 0;

        /// <summary>
        /// Flag that tells about the output encoding.
        /// </summary>
        private bool _started = false;

        //	protected BinaryWriter bw;

        /// <summary>
        /// FileStream of the process.
        /// </summary>
        private FileStream _fs;

        /// <summary>
        /// Current frame.
        /// </summary>
        private Image _image;

        /// <summary>
        /// BGR byte array from frame.
        /// </summary>
        private byte[] _pixels;

        private byte[] _indexedPixels; // converted frame indexed to palette
        private int _colorDepth; // number of bit planes
        private byte[] _colorTab; // RGB palette
        private bool[] _usedEntry = new bool[256]; // active palette entries
        private int _palSize = 7; // color table size (bits-1)
        private int _dispose = -1; // disposal code (-1 = use default)
        private bool _closeStream = false; // close stream when finished
        private bool _firstFrame = true;
        private bool _sizeSet = false; // if false, get size from first frame
        private int _sample = 10; //default sample interval for quantizer

        #endregion

        /// <summary>
        /// Sets the delay time between each frame, or changes it
        /// for subsequent frames (applies to last frame added).
        /// </summary>
        /// <param name="ms">Delay time in milliseconds</param>
        public void SetDelay(int ms)
        {
            _delay = (int)Math.Round(ms / 10.0f, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// Sets the GIF frame disposal code for the last added frame
        /// and any subsequent frames.  Default is 0 if no transparent
        /// color has been set, otherwise 2.
        /// </summary>
        /// <param name="code">Disposal code.</param>
        public void SetDispose(int code)
        {
            if (code >= 0)
            {
                _dispose = code;
            }
        }

        /// <summary>
        /// Sets the number of times the set of GIF frames
        /// should be played.  Default is 1; 0 means play
        /// indefinitely.  Must be invoked before the first
        /// image is added.
        /// </summary>
        /// <param name="iter">Number of iterations.</param>
        public void SetRepeat(int iter)
        {
            if (iter >= 0)
            {
                _repeat = iter;
            }
        }

        /// <summary>
        /// Sets the transparent color for the last added frame and any subsequent frames.
        /// Since all colors are subject to modification
        /// in the quantization process, the color in the final
        /// palette for each frame closest to the given color
        /// becomes the transparent color for that frame.
        /// May be set to null to indicate no transparent color.
        /// </summary>
        /// <param name="c">Color to be treated as transparent on display.</param>
        public void SetTransparent(Color c)
        {
            _transparent = c;
        }

        /// <summary>
        /// Adds next GIF frame.  The frame is not written immediately, but is
        /// actually deferred until the next frame is received so that timing
        /// data can be inserted.  Invoking <code>finish()</code> flushes all
        /// frames.  If <code>setSize</code> was not invoked, the size of the
        /// first image is used for all subsequent frames.
        /// </summary>
        /// <param name="im">BufferedImage containing frame to write.</param>
        /// <param name="x">The horizontal position of the frame</param>
        /// <param name="y">The vertical position of the frame</param>
        /// <returns>True if successful.</returns>
        public bool AddFrame(Image im, int x = 0, int y = 0)
        {
            if ((im == null) || !_started)
            {
                return false;
            }
            bool ok = true;

            try
            {
                if (!_sizeSet)
                {
                    //Use first frame's size.
                    SetSize(im.Width, im.Height);
                }
                _image = im;
                GetImagePixels(); //Convert to correct format if necessary.
                AnalyzePixels(); //Build color table & map pixels.

                if (_firstFrame)
                {
                    WriteLsd(); //Logical screen descriptor.
                    WritePalette(); //Global color table.

                    if (_repeat >= 0)
                    {
                        //Use Netscape app extension to indicate a gif with multiple frames.
                        WriteNetscapeExt();
                    }
                }
                WriteGraphicCtrlExt(); //Write graphic control extension.
                WriteImageDesc(im.Width, im.Height, x, y); //Image descriptor.

                if (!_firstFrame)
                {
                    WritePalette(); //Local color table.
                }

                WritePixels(im.Width, im.Height); //Encode and write pixel data.
                _firstFrame = false;

            }
            catch (IOException e)
            {
                ok = false;
            }

            return ok;
        }

        /// <summary>
        /// Flushes any pending data and closes output file. If writing to an OutputStream, the stream is not closed.
        /// </summary>
        /// <returns></returns>
        public bool Finish()
        {
            if (!_started) return false;
            bool ok = true;
            _started = false;
            try
            {
                _fs.WriteByte(0x3b); // gif trailer
                _fs.Flush();
                if (_closeStream)
                {
                    _fs.Close();
                }
            }
            catch (IOException e)
            {
                ok = false;
            }

            // reset for subsequent use
            _transIndex = 0;
            _fs = null;
            _image = null;
            _pixels = null;
            _indexedPixels = null;
            _colorTab = null;
            _closeStream = false;
            _firstFrame = true;

            return ok;
        }

        /// <summary>
        /// Sets frame rate in frames per second. Equivalent to <code>setDelay(1000/fps)</code>.
        /// </summary>
        /// <param name="fps">Frame rate (frames per second)</param>
        public void SetFrameRate(float fps)
        {
            if (fps != 0f)
            {
                _delay = (int)Math.Round(100f / fps, MidpointRounding.AwayFromZero);
            }
        }

        /// <summary>
        /// Sets quality of color quantization (conversion of images
        /// to the maximum 256 colors allowed by the GIF specification).
        /// Lower values (minimum = 1) produce better colors, but slow
        /// processing significantly.  10 is the default, and produces
        /// good color mapping at reasonable speeds.  Values greater
        /// than 20 do not yield significant improvements in speed.
        /// </summary>
        /// <param name="quality">Quality value greater than 0.</param>
        public void SetQuality(int quality)
        {
            if (quality < 1) quality = 1;
            _sample = quality;
        }

        /// <summary>
        /// Sets the GIF frame size. The default size is the
        /// size of the first frame added if this method is
        /// not invoked.
        /// </summary>
        /// <param name="w">The frame width.</param>
        /// <param name="h">The frame weight.</param>
        public void SetSize(int w, int h)
        {
            if (_started && !_firstFrame) return;
            _width = w;
            _height = h;
            if (_width < 1) _width = 320;
            if (_height < 1) _height = 240;
            _sizeSet = true;
        }

        /// <summary>
        /// Initiates GIF file creation on the given stream. The stream is not closed automatically.
        /// </summary>
        /// <param name="os">OutputStream on which GIF images are written.</param>
        /// <returns>False if initial write failed.</returns>
        public bool Start(FileStream os)
        {
            if (os == null) return false;
            bool ok = true;
            _closeStream = false;
            _fs = os;
            try
            {
                WriteString("GIF89a"); // header
            }
            catch (IOException e)
            {
                ok = false;
            }
            return _started = ok;
        }

        /// <summary>
        /// Initiates writing of a GIF file with the specified name.
        /// </summary>
        /// <param name="file">String containing output file name.</param>
        /// <returns>False if open or initial write failed.</returns>
        public bool Start(String file)
        {
            bool ok = true;
            try
            {
                //bw = new BinaryWriter( new FileStream( file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None ) );
                _fs = new FileStream(file, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                ok = Start(_fs);
                _closeStream = true;
            }
            catch (IOException e)
            {
                ok = false;
            }

            return _started = ok;
        }

        /// <summary>
        /// Analyzes image colors and creates color map.
        /// </summary>
        private void AnalyzePixels()
        {
            int len = _pixels.Length;
            int nPix = len / 3;
            _indexedPixels = new byte[nPix];

            var nq = new NeuQuant(_pixels, len, _sample);

            //Initialize quantizer.
            _colorTab = nq.Process(); //Create reduced palette

            #region Not used

            // convert map from BGR to RGB
            //for (int i = 0; i < colorTab.Length; i += 3)
            //{
            //    byte temp = colorTab[i];
            //    colorTab[i] = colorTab[i + 2];
            //    colorTab[i + 2] = temp;
            //    usedEntry[i / 3] = false;
            //}

            #endregion

            //Map image pixels to new palette.
            int k = 0;
            _usedEntry = new bool[256];//here is the fix. from the internet, codeproject.

            for (int i = 0; i < nPix; i++)
            {
                int index =
                    nq.Map(_pixels[k++] & 0xff,
                    _pixels[k++] & 0xff,
                    _pixels[k++] & 0xff);
                _usedEntry[index] = true;
                _indexedPixels[i] = (byte)index;
            }

            _pixels = null;
            _colorDepth = 8;
            _palSize = 7;

            //Get closest match to transparent color if specified.
            if (_transparent != Color.Empty)
            {
                _transIndex = FindClosest(_transparent);
                //transIndex = nq.Map(transparent.B, transparent.G, transparent.R);
            }
        }

        /// <summary>
        /// Returns index of palette color closest to given color.
        /// </summary>
        /// <param name="c">The color to search for in the pallette.</param>
        /// <returns>The index of the pallete color.</returns>
        private int FindClosest(Color c)
        {
            if (_colorTab == null) return -1;

            int r = c.R;
            int g = c.G;
            int b = c.B;
            int minpos = 0;
            int dmin = 256 * 256 * 256;
            int len = _colorTab.Length;

            for (int i = 0; i < len; )
            {
                int dr = r - (_colorTab[i++] & 0xff);
                int dg = g - (_colorTab[i++] & 0xff);
                int db = b - (_colorTab[i] & 0xff);
                int d = dr * dr + dg * dg + db * db;
                int index = i / 3;

                if (_usedEntry[index] && (d < dmin))
                {
                    dmin = d;
                    minpos = index;
                }
                i++;
            }
            return minpos;
        }

        /// <summary>
        /// Extracts image pixels into byte array "pixels".
        /// </summary>
        private void GetImagePixels()
        {
            //int w = _image.Width;
            //int h = _image.Height;
            //		int type = image.GetType().;
            //if ((w != _width) || (h != _height))
            //{
            //    // create new image with right size/format
            //    Image temp = new Bitmap(_width, _height);
            //    Graphics g = Graphics.FromImage(temp);
            //    g.DrawImage(_image, 0, 0);
            //    _image = temp;
            //    g.Dispose();
            //}

            //Performance upgrade, now encoding takes half of the time, due to Marshal calls.

            _pixels = new Byte[3 * _image.Width * _image.Height];
            int count = 0;
            var tempBitmap = new Bitmap(_image);

            var pixelUtil = new PixelUtil(tempBitmap);
            pixelUtil.LockBits();

            //Benchmark.Start();

            for (int th = 0; th < _image.Height; th++)
            {
                for (int tw = 0; tw < _image.Width; tw++)
                {
                    Color color = pixelUtil.GetPixel(tw, th);
                    _pixels[count] = color.R;
                    count++;
                    _pixels[count] = color.G;
                    count++;
                    _pixels[count] = color.B;
                    count++;
                }
            }

            pixelUtil.UnlockBits();

            //Benchmark.End();
            //Console.WriteLine(Benchmark.GetSeconds());

            //pixels = ((DataBufferByte) image.getRaster().getDataBuffer()).getData();
        }

        /// <summary>
        /// Writes Graphic Control Extension.
        /// </summary>
        private void WriteGraphicCtrlExt()
        {
            _fs.WriteByte(0x21); // extension introducer
            _fs.WriteByte(0xf9); // GCE label
            _fs.WriteByte(4); // data block size

            //Use Inplace if you want to Leave the last frame pixel.
            //#define GCE_DISPOSAL_NONE 0 //Same as "Undefined" undraw method
            //#define GCE_DISPOSAL_INPLACE 1 //Same as "Leave" undraw method in MS Gif Animator 1.01
            //#define GCE_DISPOSAL_BACKGROUND 2 //Same as "Restore background"
            //#define GCE_DISPOSAL_RESTORE 3 //Same as "Restore previous"

            //If transparency is set:
            //First frame as "Leave" with no Transparency.
            //Following frames as "Undefined" with Transparency.

            int transp = 0, disp = 0;

            if (_transparent != Color.Empty)
            {
                if (_firstFrame)
                {
                    transp = 0;

                    if (_dispose >= 0)
                    {
                        disp = _dispose & 7; // user override
                    }
                    disp <<= 2;
                }
                else
                {
                    transp = 1;
                    disp = 0;
                }
            }

            //packed fields
            _fs.WriteByte(Convert.ToByte(0 | // 1:3 reserved
                disp | // 4:6 disposal
                0 | // 7   user input - 0 = none
                transp)); // 8   transparency flag

            WriteShort(_delay); // delay x 1/100 sec
            _fs.WriteByte(Convert.ToByte(_transIndex)); // transparent color index
            _fs.WriteByte(0); // block terminator
        }

        /// <summary>
        /// Writes Image Descriptor.
        /// </summary>
        private void WriteImageDesc(int width, int heigth, int x = 0, int y = 0)
        {
            //HERE, i should set the position relative to the first changed pixel.

            _fs.WriteByte(0x2c); // image separator
            WriteShort(x); // image position x,y = 0,0
            WriteShort(y);

            //Image size
            WriteShort(width); //- was _width
            WriteShort(heigth);

            // packed fields
            if (_firstFrame)
            {
                // no LCT  - GCT is used for first (or only) frame
                _fs.WriteByte(0);
            }
            else
            {
                //fs.WriteByte(0);
                //return;

                // specify normal LCT
                _fs.WriteByte(Convert.ToByte(0x80 | // 1 local color table  1=yes
                    0 | // 2 interlace - 0=no
                    0 | // 3 sorted - 0=no
                    0 | // 4-5 reserved
                    _palSize)); // 6-8 size of color table
            }
        }

        /// <summary>
        /// Writes Logical Screen Descriptor
        /// </summary>
        private void WriteLsd()
        {
            // logical screen size
            WriteShort(_width);
            WriteShort(_height);
            // packed fields

            _fs.WriteByte(Convert.ToByte(0x80 | // 1   : global color table flag = 1 (gct used)
                0x70 | // 2-4 : color resolution = 7
                0x00 | // 5   : gct sort flag = 0
                _palSize)); // 6-8 : gct size

            _fs.WriteByte(0); // background color index
            _fs.WriteByte(0); // pixel aspect ratio - assume 1:1
        }

        /// <summary>
        /// Writes Netscape application extension to define the repeat count.
        /// </summary>
        private void WriteNetscapeExt()
        {
            _fs.WriteByte(0x21); // extension introducer
            _fs.WriteByte(0xff); // app extension label
            _fs.WriteByte(11); // block size
            WriteString("NETSCAPE" + "2.0"); // app id + auth code
            _fs.WriteByte(3); // sub-block size
            _fs.WriteByte(1); // loop sub-block id
            WriteShort(_repeat); // loop count (extra iterations, 0=repeat forever) //-1 no repeat, 0 = forever, 1=once... n=extra repeat
            _fs.WriteByte(0); // block terminator
        }

        /// <summary>
        /// Writes color table.
        /// </summary>
        private void WritePalette()
        {
            _fs.Write(_colorTab, 0, _colorTab.Length);
            int n = (3 * 256) - _colorTab.Length;
            for (int i = 0; i < n; i++)
            {
                _fs.WriteByte(0);
            }
        }

        /// <summary>
        /// Encodes and writes pixel data.
        /// </summary>
        private void WritePixels(int width, int height)
        {
            var encoder = new LZWEncoder(width, height, _indexedPixels, _colorDepth);
            encoder.Encode(_fs);
        }

        /// <summary>
        /// Writes the comment for the animation.
        /// </summary>
        /// <param name="comment">The Comment to write.</param>
        private void WriteComment(string comment)
        {
            _fs.WriteByte(0x21);
            _fs.WriteByte(0xfe);

            //byte[] lenght = StringToByteArray(comment.Length.ToString("X"));

            //foreach (byte b in lenght)
            //{
            //    fs.WriteByte(b);
            //}

            var bytes = System.Text.Encoding.ASCII.GetBytes(comment);
            _fs.WriteByte((byte)bytes.Length);
            _fs.Write(bytes, 0, bytes.Length);
            _fs.WriteByte(0);
            //WriteString(comment);
        }

        /// <summary>
        /// Converts a String to a byte Array.
        /// </summary>
        /// <param name="hex">The string to convert</param>
        /// <returns>A byte array corresponding to the string</returns>
        public static byte[] StringToByteArray(String hex)
        {
            if ((hex.Length % 2) == 1) //if odd
            {
                hex = hex.PadLeft(1, '0');
            }

            int numberChars = hex.Length / 2;
            var bytes = new byte[numberChars];
            using (var sr = new StringReader(hex))
            {
                for (int i = 0; i < numberChars; i++)
                    bytes[i] =
                      Convert.ToByte(new string(new char[2] { (char)sr.Read(), (char)sr.Read() }), 16);
            }
            return bytes;
        }

        /// <summary>
        /// Write 16-bit value to output stream, LSB first.
        /// </summary>
        /// <param name="value">The 16-bit value.</param>
        private void WriteShort(int value)
        {
            _fs.WriteByte(Convert.ToByte(value & 0xff));
            _fs.WriteByte(Convert.ToByte((value >> 8) & 0xff));
        }

        /// <summary>
        /// Writes string to output stream.
        /// </summary>
        /// <param name="s">The string to write.</param>
        private void WriteString(String s)
        {
            char[] chars = s.ToCharArray();
            foreach (char t in chars)
            {
                _fs.WriteByte((byte)t);
            }
        }

        public void Dispose()
        {
            _started = false;
            try
            {
                WriteComment("Made with ScreenToGif");

                //Gif trailer, end of the gif.
                //fs.WriteByte(0x00);
                _fs.WriteByte(0x3b);

                _fs.Flush();
                if (_closeStream)
                {
                    _fs.Close();
                }
            }
            catch (IOException e)
            { }

            // reset for subsequent use
            _transIndex = 0;
            _fs = null;
            _image = null;
            _pixels = null;
            _indexedPixels = null;
            _colorTab = null;
            _closeStream = false;
            _firstFrame = true;

        }
    }
}