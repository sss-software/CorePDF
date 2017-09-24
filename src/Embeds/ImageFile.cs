﻿using ICSharpCode.SharpZipLib.Zip.Compression.Streams;
using Newtonsoft.Json;
using SixLabors.ImageSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.XPath;

namespace CorePDF.Embeds
{
    /// <summary>
    /// Defines the images that are going to be included in the document.
    /// </summary>
    public class ImageFile : PDFObject
    {
        public const string IMAGEMASK = "imagemask";
        public const string IMAGEDATA = "imagedata";
        public const string PATHDATA = "pathdata";

        private int _bitsPerComponent { get; set; } = 8;

        [JsonIgnore]
        public ImageFile MaskData { get; set; }

        /// <summary>
        /// The name used to reference this image in the document content
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// A fully qualified path to the image file
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// The file type of image object being included.
        /// </summary>
        public string Type { get; set; } = IMAGEDATA;

        /// <summary>
        /// The RBG data for the image. If a filename is provided then this field will
        /// be calculated upon import of the files. 
        /// </summary>
        public byte[] ByteData { get; set; }

        /// <summary>
        /// Width of the image in pixels. This is used to apply the scale factor when
        /// Placing the image on the page. Used to ensure printed image retains proper
        /// aspect ratio.
        /// This field is calculated when the image file is embedded.
        /// </summary>
        [JsonIgnore]
        public int Width { get; set; }

        /// <summary>
        /// Height of the image in pixels. This is used to apply the scale factor when
        /// Placing the image on the page. Used to ensure printed image retains proper
        /// aspect ratio.
        /// This field is calculated when the image file is embedded.
        /// </summary>
        [JsonIgnore] 
        public int Height { get; set; }

        /// <summary>
        /// Determines if a vector image should be rasterised for display
        /// </summary>
        public bool Rasterize { get; set; } = true;

        public void EmbedFile()
        {
            // do nothing if there is no valid file specified
            if (string.IsNullOrEmpty(FilePath) || !File.Exists(FilePath)) return;

            if (!Rasterize)
            {
                using (var fileStream = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
                {
                    _encodedData = new byte[fileStream.Length];
                    fileStream.Read(_encodedData, 0, (int)fileStream.Length);
                    fileStream.Position = 0;

                    Type = PATHDATA;

                    var sourceSVG = new XmlDocument();
                    sourceSVG.Load(fileStream);

                    //var sourceSVG = new XPathDocument(fileStream);
                    var nav = sourceSVG.CreateNavigator();
                    var svgns = "";
                    var foundRoot = false;

                    if (nav.MoveToChild("svg", "http://www.w3.org/2000/svg"))
                    {
                        foundRoot = true;
                        svgns = "http://www.w3.org/2000/svg";
                    }
                    else
                    {
                        foundRoot = nav.MoveToChild("svg", svgns);
                    }

                    if (foundRoot)
                    {
                        decimal.TryParse(nav.GetAttribute("width", ""), out decimal width);
                        decimal.TryParse(nav.GetAttribute("height", ""), out decimal height);

                        if (height == 0 || width == 0)
                        {
                            // no height or width specified check for a viewbox
                            var viewBox = nav.GetAttribute("viewBox", "");
                            if (!string.IsNullOrEmpty(viewBox))
                            {
                                var dimensions = viewBox.Split(' ');

                                if (dimensions.Length == 4)
                                {
                                    width = decimal.Parse(dimensions[2]);
                                    height = decimal.Parse(dimensions[3]);
                                }
                            }
                            else
                            {
                                throw new Exception("SVG does not specify any dimensions");
                            }
                        }

                        Height = (int)height;
                        Width = (int)width;

                        // go through the polygons and convert them to paths
                        var polygons = nav.SelectChildren("polygon", svgns);
                        while (polygons.MoveNext())
                        {
                            var polygon = polygons.Current.GetAttribute("points","");
                            polygon = polygon.Replace(",", " ");

                            var points = polygon.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            var path = "M ";
                            for (var i = 0; i < points.Length; i++ )
                            {
                                if (i == 2)
                                {
                                    path += "L ";
                                }

                                path += points[i].ToString() + " ";
                            }
                            path += "Z";

                            polygons.Current.ReplaceSelf(string.Format("<path fill='none' d='{0}' />", path));
                        }

                        // go through the rectangles and convert them to paths also
                        var rects = nav.SelectChildren("rect", svgns);
                        while (rects.MoveNext())
                        {
                            var startX = rects.Current.GetAttribute("x", "");
                            var startY = rects.Current.GetAttribute("y", "");
                            var rectWidth = rects.Current.GetAttribute("width", "");
                            var rectHeight = rects.Current.GetAttribute("width", "");
                            var rectFill = rects.Current.GetAttribute("fill", "");
                            if (string.IsNullOrEmpty(rectFill))
                            {
                                rectFill = "fill='none'";
                            }
                            else
                            {
                                rectFill = string.Format("fill='{0}'", rectFill);
                            }
                            var rectStroke = rects.Current.GetAttribute("stroke", "");
                            if (!string.IsNullOrEmpty(rectStroke))
                            {
                                rectStroke = string.Format("stroke='{0}'", rectStroke);
                            }
                            var rectStrokeWidth = rects.Current.GetAttribute("stroke-width", "");
                            if (!string.IsNullOrEmpty(rectStrokeWidth))
                            {
                                rectStrokeWidth = string.Format("stroke-width='{0}'", rectStrokeWidth);
                            }

                            var path = "M ";
                            path += string.Format("{0} {1} h {2} v {3} h -{4} ", startX, startY, width, height, width);
                            path += "Z ";
                            rects.Current.ReplaceSelf(string.Format("<path d='{0}' {1} {2} {3} />", path, rectFill, rectStroke, rectStrokeWidth));
                        }

                        var paths = nav.SelectChildren("path", svgns);
                        var result = new TokenisedSVG();
                        result.Paths.Add(new PDFPath("[] 0 d\n"));

                        while (paths.MoveNext())
                        {
                            var path = paths.Current.GetAttribute("d", "");

                            var style = paths.Current.GetAttribute("style", "");
                            if (string.IsNullOrEmpty(style))
                            {
                                var fill = paths.Current.GetAttribute("fill", "");

                                if (fill.ToLower() == "none")
                                {
                                    // fill with white if shape is supposed to be hollow
                                    result.Paths.Add(new PDFPath("1 1 1 rg\n"));
                                }
                                else
                                {
                                    if (! string.IsNullOrEmpty(fill))
                                    {
                                        result.Paths.Add(new PDFPath(string.Format("{0} rg\n", ToPDFColor(fill))));
                                    }
                                }

                                var strokeColor = paths.Current.GetAttribute("stroke", "");
                                if (!string.IsNullOrEmpty(strokeColor))
                                {
                                    result.Paths.Add(new PDFPath(string.Format("{0} RG\n", ToPDFColor(strokeColor))));
                                }
                                else
                                {
                                    result.Paths.Add(new PDFPath("0 0 0 RG\n"));
                                }

                                var strokeWidth = 1m;
                                if (!string.IsNullOrEmpty(paths.Current.GetAttribute("stroke-width", "")))
                                {
                                    decimal.TryParse(paths.Current.GetAttribute("stroke-width", ""), out strokeWidth);
                                }
                                result.Paths.Add(new PDFPath(string.Format("{0} w\n", strokeWidth)));
                            }
                            else
                            {
                                // todo: handle css style interpretation here
                            }


                            // interpret the path
                            path = path.Replace(",", " ");
                            path = path.Replace("-", " -");
                            path = path.Replace("-.", "-0.");
                            path = path.Replace("\n", " ");
                            path = path.Replace("\r", " ");
                            path = path.Replace("M", " M "); // Move To
                            path = path.Replace("m", " m ");
                            path = path.Replace("Z", " Z "); // Closepath
                            path = path.Replace("z", " z ");
                            path = path.Replace("L", " L "); // Line
                            path = path.Replace("l", " l ");
                            path = path.Replace("H", " H "); // Horizontal
                            path = path.Replace("h", " h ");
                            path = path.Replace("V", " V "); // Vertical
                            path = path.Replace("v", " v ");
                            path = path.Replace("C", " C "); // Cubic Bezier curve
                            path = path.Replace("c", " c ");
                            path = path.Replace("S", " S "); // Smoothed CBC TODO
                            path = path.Replace("s", " s ");
                            path = path.Replace("Q", " Q "); // Quadratic Bezier curve
                            path = path.Replace("q", " q ");
                            path = path.Replace("T", " T "); // Smoothed QBC TODO
                            path = path.Replace("t", " t ");
                            path = path.Replace("A", " A "); // Eliptical Arc TODO
                            path = path.Replace("a", " a ");

                            var pathElements = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            var position = 0;
                            var posX = 0m;
                            var posY = 0m;
                            var startPosX = 0m;
                            var startPosY = 0m;
                            var inLineMode = false;
                            var relative = false;

                            // TODO: polygons need to become paths
                            while (position < pathElements.Length)
                            {
                                var cx1 = 0m;
                                var cx2 = 0m;
                                var cy1 = 0m;
                                var cy2 = 0m;

                                var element = pathElements[position];
                                switch (element)
                                {
                                    case "A":
                                    case "a":
                                        position++;
                                        var rx = double.Parse(pathElements[position]); // x-radius
                                        position++;
                                        var ry = double.Parse(pathElements[position]); // y-radius
                                        position++;
                                        var xRot = double.Parse(pathElements[position]); // x-rotation
                                        position++;
                                        var largeArc = int.Parse(pathElements[position]); // large-arc flag
                                        position++;
                                        var sweep = int.Parse(pathElements[position]); // sweep flag 

                                        var startX = (double)posX;
                                        var startY = (double)posY;

                                        // execute the elliptical arc command
                                        if (element == "a")
                                        {
                                            // relative
                                            position++;
                                            posX = posX + decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = posY + (0 - decimal.Parse(pathElements[position]));
                                        }
                                        else
                                        {
                                            // absolute
                                            position++;
                                            posX = decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = height - decimal.Parse(pathElements[position]);
                                        }

                                        var endX = (double)posX;
                                        var endY = (double)posY;

                                        if (endY > startY)
                                        {
                                            // swap the sweep flag around if large arc is specified
                                            // because the y Axis is reversed 
                                            if (sweep == 1)
                                            {
                                                sweep = 0;
                                            }
                                            else
                                            {
                                                sweep = 1;
                                            }
                                        }

                                        var curves = ArcToBezier(new Point(startX, startY), new Point(endX, endY), rx, ry, xRot, largeArc, sweep);

                                        foreach(var curve in curves)
                                        {
                                            result.Paths.Add(new PDFPath("{0} {1} {2} {3} {4} {5} c\n", new List<PDFPathParam>()
                                            {
                                                new PDFPathParam
                                                {
                                                    Value = (decimal)curve.Cp1.X,
                                                    Operation = "+offsetX; *scale"
                                                },
                                                new PDFPathParam
                                                {
                                                    Value = (decimal)curve.Cp1.Y,
                                                    Operation = "+offsetY; *scale"
                                                },
                                                new PDFPathParam
                                                {
                                                    Value = (decimal)curve.Cp2.X,
                                                    Operation = "+offsetX; *scale"
                                                },
                                                new PDFPathParam
                                                {
                                                    Value = (decimal)curve.Cp2.Y,
                                                    Operation = "+offsetY; *scale"
                                                },
                                                new PDFPathParam
                                                {
                                                    Value = (decimal)curve.End.X,
                                                    Operation = "+offsetX; *scale"
                                                },
                                                new PDFPathParam
                                                {
                                                    Value = (decimal)curve.End.Y,
                                                    Operation = "+offsetY; *scale"
                                                }
                                            }));
                                        }

                                        inLineMode = false;
                                        break;

                                    case "M":
                                    case "m":
                                        // execute the move-to command
                                        if (element == "m")
                                        {
                                            // relative
                                            position++;
                                            posX = posX + decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = posY + (0 - decimal.Parse(pathElements[position]));
                                        }
                                        else
                                        {
                                            // absolute
                                            position++;
                                            posX = decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = height - decimal.Parse(pathElements[position]);
                                        }

                                        result.Paths.Add(new PDFPath("{0} {1} m\n", new List<PDFPathParam>()
                                        {
                                            new PDFPathParam
                                            {
                                                Value = posX,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = posY,
                                                Operation = "+offsetY; *scale"
                                            }
                                        }));

                                        startPosX = posX;
                                        startPosY = posY;

                                        break;
                                    case "Z":
                                    case "z":
                                        // execute the close-path command
                                        result.Paths.Add(new PDFPath("{0} {1} l\n", new List<PDFPathParam>()
                                        {
                                            new PDFPathParam
                                            {
                                                Value = startPosX,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = startPosY,
                                                Operation = "+offsetY; *scale"
                                            }
                                        }));

                                        posX = startPosX;
                                        posY = startPosY;

                                        break;
                                    case "L":
                                    case "l":
                                        // execute the line-to command
                                        if (element == "l")
                                        {
                                            // relative
                                            position++;
                                            posX = posX + decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = posY + (0 - decimal.Parse(pathElements[position]));

                                            relative = true;
                                        }
                                        else
                                        {
                                            // absolute
                                            position++;
                                            posX = decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = height - decimal.Parse(pathElements[position]);
                                        }

                                        result.Paths.Add(new PDFPath("{0} {1} l\n", new List<PDFPathParam>()
                                        {
                                            new PDFPathParam
                                            {
                                                Value = posX,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = posY,
                                                Operation = "+offsetY; *scale"
                                            }
                                        }));

                                        inLineMode = true;

                                        break;
                                    case "H":
                                    case "h":
                                        // execute the line-to command
                                        if (element == "h")
                                        {
                                            // relative
                                            position++;
                                            posX = posX + decimal.Parse(pathElements[position]);

                                            relative = true;
                                        }
                                        else
                                        {
                                            // absolute
                                            position++;
                                            posX = decimal.Parse(pathElements[position]);
                                        }

                                        result.Paths.Add(new PDFPath("{0} {1} l\n", new List<PDFPathParam>()
                                        {
                                            new PDFPathParam
                                            {
                                                Value = posX,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = posY,
                                                Operation = "+offsetY; *scale"
                                            }
                                        }));

                                        inLineMode = true;

                                        break;
                                    case "V":
                                    case "v":
                                        // execute the line-to command
                                        if (element == "v")
                                        {
                                            // relative
                                            position++;
                                            posY = posY + (0 - decimal.Parse(pathElements[position]));

                                            relative = true;
                                        }
                                        else
                                        {
                                            // absolute
                                            position++;
                                            posY = height - decimal.Parse(pathElements[position]);
                                        }

                                        result.Paths.Add(new PDFPath("{0} {1} l\n", new List<PDFPathParam>()
                                        {
                                            new PDFPathParam
                                            {
                                                Value = posX,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = posY,
                                                Operation = "+offsetY; *scale"
                                            }
                                        }));

                                        inLineMode = true;
                                        break;

                                    case "C":
                                    case "c":
                                        // execute the cubic bezier command

                                        if (element == "c")
                                        {
                                            // relative
                                            position++;
                                            cx1 = posX + decimal.Parse(pathElements[position]);
                                            position++;
                                            cy1 = posY + (0 - decimal.Parse(pathElements[position]));

                                            position++;
                                            cx2 = posX + decimal.Parse(pathElements[position]);
                                            position++;
                                            cy2 = posY + (0 - decimal.Parse(pathElements[position]));

                                            position++;
                                            posX = posX + decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = posY + (0 - decimal.Parse(pathElements[position]));
                                        }
                                        else
                                        {
                                            // absolute
                                            position++;
                                            cx1 = decimal.Parse(pathElements[position]);
                                            position++;
                                            cy1 = height - decimal.Parse(pathElements[position]);

                                            position++;
                                            cx2 = decimal.Parse(pathElements[position]);
                                            position++;
                                            cy2 = height - decimal.Parse(pathElements[position]);

                                            position++;
                                            posX = decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = height - decimal.Parse(pathElements[position]);
                                        }

                                        result.Paths.Add(new PDFPath("{0} {1} {2} {3} {4} {5} c\n", new List<PDFPathParam>()
                                        {
                                            new PDFPathParam
                                            {
                                                Value = cx1,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = cy1,
                                                Operation = "+offsetY; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = cx2,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = cy2,
                                                Operation = "+offsetY; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = posX,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = posY,
                                                Operation = "+offsetY; *scale"
                                            }
                                        }));

                                        break;

                                    case "Q":
                                    case "q":
                                        // execute the quadratic bezier command
                                        var qx1 = 0m;
                                        var qy1 = 0m;
                                        var x1 = 0m;
                                        var x2 = 0m;
                                        var y1 = 0m;
                                        var y2 = 0m;

                                        if (element == "q")
                                        {
                                            // relative
                                            position++;
                                            qx1 = posX + decimal.Parse(pathElements[position]);
                                            position++;
                                            qy1 = posY + (0 - decimal.Parse(pathElements[position]));

                                            // need to calculate the first cubic control points from start position 
                                            // and the quadratic control point
                                            x1 = posX + (2 / 3 * (qx1 - posX));
                                            y1 = posY + (2 / 3 * (qy1 - posY));

                                            position++;
                                            posX = posX + decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = posY + (0 - decimal.Parse(pathElements[position]));
                                        }
                                        else
                                        {
                                            // absolute
                                            position++;
                                            qx1 = decimal.Parse(pathElements[position]);
                                            position++;
                                            qy1 = height - decimal.Parse(pathElements[position]);

                                            // need to calculate the first cubic control points from start position 
                                            // and the quadratic control point
                                            x1 = posX + (2 / 3 * (qx1 - posX));
                                            y1 = posY + (2 / 3 * (qy1 - posY));

                                            position++;
                                            posX = decimal.Parse(pathElements[position]);
                                            position++;
                                            posY = height - decimal.Parse(pathElements[position]);
                                        }

                                        // need to calculate the second cubic control points from end position 
                                        // and the quadratic control point
                                        x2 = posX + (2 / 3 * (qx1 - posX));
                                        y2 = posY + (2 / 3 * (qy1 - posY));

                                        result.Paths.Add(new PDFPath("{0} {1} {2} {3} {4} {5} c\n", new List<PDFPathParam>()
                                        {
                                            new PDFPathParam
                                            {
                                                Value = x1,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = y1,
                                                Operation = "+offsetY; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = x2,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = y2,
                                                Operation = "+offsetY; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = posX,
                                                Operation = "+offsetX; *scale"
                                            },
                                            new PDFPathParam
                                            {
                                                Value = posY,
                                                Operation = "+offsetY; *scale"
                                            }
                                        }));

                                        break;

                                    default:
                                        // not a command so it must be a set of coordinates
                                        if (inLineMode)
                                        {
                                            if (relative)
                                            {
                                                posX = posX + decimal.Parse(pathElements[position]);
                                                position++;
                                                posY = posY + (0 - decimal.Parse(pathElements[position]));
                                            }
                                            else
                                            {
                                                posX = decimal.Parse(pathElements[position]);
                                                position++;
                                                posY = height - decimal.Parse(pathElements[position]);
                                            }

                                            result.Paths.Add(new PDFPath("{0} {1} l\n", new List<PDFPathParam>()
                                            {
                                                new PDFPathParam
                                                {
                                                    Value = posX,
                                                    Operation = "+offsetX; *scale"
                                                },
                                                new PDFPathParam
                                                {
                                                    Value = posY,
                                                    Operation = "+offsetY; *scale"
                                                }
                                            }));

                                        }
                                        break;
                                }

                                position++;
                            }

                            result.Paths.Add(new PDFPath("b\n"));
                        }


                        ByteData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(result));
                    }

                }

                return;
            }

            using (var image = Image.Load(FilePath))
            {
                Height = image.Height;
                Width = image.Width;

                var hasAlpha = false;

                var rgbbuf = image.SavePixelData();

                var abuf = new byte[rgbbuf.Length];
                var rbuf = new byte[rgbbuf.Length];

                var i = 0;
                var a = 0;
                var r = 0;
                while (i < rgbbuf.Length)
                {
                    var bytes = new byte[4];
                    bytes[0] = rgbbuf[i];
                    i++;
                    bytes[1] = rgbbuf[i];
                    i++;
                    bytes[2] = rgbbuf[i];
                    i++;
                    bytes[3] = rgbbuf[i];
                    i++;

                    if (bytes[3] < 255)
                    {
                        hasAlpha = true;
                    }

                    rbuf[r] = bytes[0];
                    r++;
                    rbuf[r] = bytes[1];
                    r++;
                    rbuf[r] = bytes[2];
                    r++;
                    abuf[a] = bytes[3];
                    a++;
                }

                rgbbuf = new byte[r];

                if (hasAlpha)
                {
                    MaskData = new ImageFile()
                    {
                        ByteData = new byte[a],
                        Height = Height,
                        Width = Width,
                        Type = IMAGEMASK
                    };

                    Array.Copy(abuf, MaskData.ByteData, a);
                }

                Array.Copy(rbuf, rgbbuf, r);
                ByteData = rgbbuf;
            }

        }

        public override void PrepareStream(bool compress = false)
        {
            if (Rasterize)
            {
                // only need to do this if the image has been rasterised
                // if it hasn't then the file has already been encoded 
                // into the PDF object
                _encodedData = ByteData;
            }

            base.PrepareStream(compress);

            if (MaskData != null)
            {
                MaskData.PrepareStream(compress);
            }
        }

        public override void Publish(StreamWriter stream)
        {
            if (Rasterize)
            {
                // save the image data
                var PDFData = new Dictionary<string, dynamic>
                {
                    { "/Type", "/XObject" },
                    { "/Subtype", "/Image"},
                    { "/Name", "/" + Id},
                    { "/Width", Width.ToString()},
                    { "/Height", Height.ToString()},
                    { "/BitsPerComponent", _bitsPerComponent.ToString()}
                };

                switch (Type)
                {
                    case IMAGEDATA:
                        PDFData.Add("/ColorSpace", "/DeviceRGB");
                        PDFData.Add("/Interpolate", "true");
                        if (MaskData != null)
                        {
                            PDFData.Add("/SMask", string.Format("{0} 0 R", MaskData.ObjectNumber));
                        }
                        break;

                    case IMAGEMASK:
                        PDFData.Add("/ColorSpace", "/DeviceGray");
                        break;
                }

                _pdfObject = PDFData;
            }
            else
            {
                // save the svg file here
                var PDFData = new Dictionary<string, dynamic>
                {
                    { "/Type", "/EmbeddedFile" },
                    { "/Subtype", "/image#2Fsvg+xml"},
                };

                _pdfObject = PDFData;
            }

            base.Publish(stream);

            if (MaskData != null)
            {
                // if there is an associated mask then publish that as well
                MaskData.Publish(stream);
            }
        }

        const double TAU = Math.PI * 2f;

        private Point MapToEllipse(Point p, double rx, double ry, double cosphi, double sinphi, Point centrePos)
        {
            p.X *= rx;
            p.Y *= ry;

            var xp = (cosphi * p.X) - (sinphi * p.Y);
            var yp = (sinphi * p.X) + (cosphi * p.Y);

            return new Point(xp + centrePos.X, yp + centrePos.Y);
        }

        private List<Point> ApproximateUnitArc(double angle1, double angle2)
        {
            var alpha = 4f / 3f * Math.Tan(angle2 / 4f);

            var x1 = Math.Cos(angle1);
            var y1 = Math.Sin(angle1);
            var x2 = Math.Cos(angle1 + angle2);
            var y2 = Math.Sin(angle1 + angle2);

            return new List<Point>
            {
                new Point(x1 - y1 * alpha, y1 + x1 * alpha),
                new Point(x2 + y2 * alpha, y2 - x2 * alpha),
                new Point(x2, y2)
            };
        }

        private double UnitVectorAngle(Point u, Point v)
        {
            var sign = (u.X * v.Y - u.Y * v.X < 0) ? -1 : 1;

            var umag = Math.Sqrt(u.X * u.X + u.Y * u.Y);
            var vmag = Math.Sqrt(v.X * v.X + v.Y * v.Y);

            var div = Dot(u, v) / (umag * vmag);

            if (div > 1)
            {
                div = 1;
            }
            if (div < -1)
            {
                div = -1;
            }

            return sign * Math.Acos(div);
        }

        private Arc GetArcCentre(Point p, Point c, double rx, double ry, int largeArc, int sweep, double sinphi, double cosphi, Point pp)
        {
            var result = new Arc();

            var rxsq = rx * rx;
            var rysq = ry * ry;
            var pxpsq = pp.X * pp.X;
            var pypsq = pp.Y * pp.Y;

            var radicant = (rxsq * rysq) - (rxsq * pypsq) - (rysq * pxpsq);
            if (radicant < 0)
            {
                radicant = 0;
            }

            radicant /= (rxsq * pypsq) + (rysq * pxpsq);
            radicant = Math.Sqrt(radicant) * (largeArc == sweep ? -1 : 1);

            var centrep = new Point()
            {
                X = radicant * rx / ry * pp.Y,
                Y = radicant * -ry / rx * pp.X
            };

            result.Centre.X = cosphi * centrep.X - sinphi * centrep.Y + (p.X + c.X) / 2;
            result.Centre.Y = sinphi * centrep.X + cosphi * centrep.Y + (p.Y + c.Y) / 2;

            var v1 = new Point()
            {
                X = (pp.X - centrep.X) / rx,
                Y = (pp.Y - centrep.Y) / ry
            };

            var v2 = new Point()
            {
                X = (-pp.X - centrep.X) / rx,
                Y = (-pp.Y - centrep.Y) / ry
            };

            result.Angle1 = UnitVectorAngle(new Point(1, 0), v1);
            result.Angle2 = UnitVectorAngle(v1, v2);

            if (sweep == 0 && result.Angle2 > 0)
            {
                result.Angle2 -= TAU;
            }

            if (sweep == 1 && result.Angle2 < 0)
            {
                result.Angle2 += TAU;
            }

            return result;
        }

        private List<Curve> ArcToBezier(Point p, Point c, double rx, double ry, double rotation = 0, int largeArc = 0, int sweep = 0)
        {
            var result = new List<Curve>();

            // if either radius is zero do nothing
            if (rx == 0 || ry == 0) return result;

            var sinphi = Math.Sin(rotation * TAU / 360);
            var cosphi = Math.Cos(rotation * TAU / 360);

            var pp = new Point()
            {
                X = (cosphi * (p.X - c.X) / 2) + (sinphi * (p.Y - c.Y) / 2),
                Y = (-sinphi * (p.X - c.X) / 2) + (cosphi * (p.Y - c.Y) / 2)
            };

            // the line isn't going anywhere since it's start and end psition are same
            if (pp.X == 0 && pp.Y == 0) return result;

            rx = Math.Abs(rx);
            ry = Math.Abs(ry);

            var lambda = ((pp.X * pp.X) / (rx * rx)) + ((pp.Y * pp.Y) / (ry * ry));
            if (lambda > 1)
            {
                rx *= Math.Sqrt(lambda);
                ry *= Math.Sqrt(lambda);
            }

            var arc = GetArcCentre(p, c, rx, ry, largeArc, sweep, sinphi, cosphi, pp);

            var segments = Math.Max(Math.Ceiling(Math.Abs(arc.Angle2) / (TAU / 4)), 1);
            arc.Angle2 /= segments;

            for (var i = 0; i < segments; i++)
            {
                var approx = ApproximateUnitArc(arc.Angle1, arc.Angle2);
                arc.Angle1 += arc.Angle2;

                var cp1 = MapToEllipse(approx[0], rx, ry, cosphi, sinphi, arc.Centre);
                var cp2 = MapToEllipse(approx[1], rx, ry, cosphi, sinphi, arc.Centre);
                var end = MapToEllipse(approx[2], rx, ry, cosphi, sinphi, arc.Centre);

                result.Add(new Curve()
                {
                    Cp1 = cp1,
                    Cp2 = cp2,
                    End = end
                });
            }

            return result;
        }

        private bool PointInRectangle(Point m, Rectangle r)
        {
            var AB = Vector(r.A, r.B);
            var AM = Vector(r.A, m);
            var BC = Vector(r.B, r.C);
            var BM = Vector(r.B, m);
            var dotABAM = Dot(AB, AM);
            var dotABAB = Dot(AB, AB);
            var dotBCBM = Dot(BC, BM);
            var dotBCBC = Dot(BC, BC);
            return 0 <= dotABAM && dotABAM <= dotABAB && 0 <= dotBCBM && dotBCBM <= dotBCBC;
        }

        private Point Vector(Point p1, Point p2)
        {
            return new Point
            {
                X = (p2.X - p1.X),
                Y = (p2.Y - p1.Y)
            };
        }

        private double Dot(Point u, Point v)
        {
            return u.X * v.X + u.Y * v.Y;
        }

    }

}
