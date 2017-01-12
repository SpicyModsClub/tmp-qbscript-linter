using System;
using System.IO;
using System.Text;

using MiscUtil.Conversion;
using MiscUtil.IO;

namespace QbScript_Linter
{
    class Program
    {
        private static bool errorFoundThisFile;

        private static string AppName
        {
            get
            {
                string name = AppDomain.CurrentDomain.FriendlyName;
                if (name.Contains(" "))
                {
                    return "\"" + name + "\"";
                }
                return name;
            }
        }

        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("No file arguments given!");
                Console.WriteLine("Usage: {0} [file1] [file2] ...", AppName);
                Console.WriteLine();
            }

            foreach (var file in args)
            {
                errorFoundThisFile = false;

                Console.WriteLine("=== {0} ===", file);
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Exists || fi.Attributes.HasFlag(FileAttributes.Directory))
                    {
                        throw new Exception("File does not exist");
                    }

                    using (var f = File.OpenRead(file))
                    using (var br = new EndianBinaryReader(EndianBitConverter.Little, f))
                    {
                        long fileLength = f.Length;
                        long position = 0;
                        int dictNesting = 0;
                        int arrayNesting = 0;
                        int parenNesting = 0;

                        while (position < fileLength)
                        {
                            byte c = br.ReadByte();
                            uint length;
                            int offset;

                            switch (c)
                            {
                                case 0x03:
                                    dictNesting++;
                                    break;
                                case 0x04:
                                    dictNesting--;
                                    CheckNesting(dictNesting, "dict", position);
                                    break;
                                case 0x05:
                                    arrayNesting++;
                                    break;
                                case 0x06:
                                    arrayNesting--;
                                    CheckNesting(arrayNesting, "array", position);
                                    break;
                                case 0x0E:
                                    parenNesting++;
                                    break;
                                case 0x0F:
                                    parenNesting--;
                                    CheckNesting(parenNesting, "paren", position);
                                    break;
                                case 0x16:
                                case 0x17:
                                case 0x18:
                                case 0x1A:
                                case 0x2E:
                                    // Each of these opcodes takes a 4-byte argument.  Skip it.
                                    br.ReadInt32();
                                    break;
                                case 0x1B:
                                    length = br.ReadUInt32();
                                    if (length == 0)
                                    {
                                        PrintScriptError("Zero-length string (no null terminator)", position);
                                        continue;
                                    }
                                    br.Seek((int)length - 1, SeekOrigin.Current);
                                    c = br.ReadByte();
                                    if (c != 0)
                                    {
                                        PrintScriptError("Narrow string not null-terminated", position);
                                    }
                                    break;
                                case 0x1E:
                                    // Vector3 of float, skip the 3 floats
                                    br.ReadBytes(12);
                                    break;
                                case 0x1F:
                                    // Vector2 of float, skip the 2 floats
                                    br.ReadBytes(8);
                                    break;
                                case 0x24:
                                    if (f.Position < fileLength)
                                    {
                                        PrintScriptError("End of script found before end of file.", position);
                                    }
                                    break;
                                case 0x27:
                                    position = f.Position;
                                    CheckIfOffset(br, "elseif", position);
                                    
                                    position += 2;
                                    CheckEndIfOffset(br, "elseif", position);
                                    br.Seek((int) position + 2, SeekOrigin.Begin);
                                    break;
                                case 0x2F:
                                    length = br.ReadUInt16();
                                    br.Seek((int)length*8, SeekOrigin.Current);
                                    break;
                                case 0x3E:
                                    if (br.Read() != 0x49)
                                    {
                                        PrintScriptError("Case is missing short jump", position);
                                    }
                                    position = f.Position;
                                    offset = br.ReadUInt16();
                                    br.Seek(offset-2, SeekOrigin.Current);
                                    c = br.ReadByte();
                                    if (c < 0x3D || c > 0x3F)
                                    {
                                        PrintScriptError("Case's short jump does not point to next branch", position);
                                    }
                                    br.Seek((int)position+2, SeekOrigin.Begin);
                                    break;
                                case 0x47:
                                    position = f.Position;
                                    CheckIfOffset(br, "if", position);
                                    break;
                                case 0x48:
                                    position = f.Position;
                                    CheckEndIfOffset(br, "else", position);
                                    br.Seek((int)position+2, SeekOrigin.Begin);
                                    break;
                                case 0x49:
                                    offset = br.ReadUInt16();
                                    br.Seek((int)offset - 4, SeekOrigin.Current);
                                    if (br.ReadInt16() != 0x3D01)
                                    {
                                        PrintScriptError("Short jump does not point to end switch", position);
                                    }
                                    br.Seek((int) position + 3, SeekOrigin.Begin);
                                    break;
                                case 0x4A:
                                    int padLength = 0;
                                    length = br.ReadUInt16();
                                    while (br.Read() == 0)
                                    {
                                        padLength++;
                                    }
                                    
                                    br.Seek(-3, SeekOrigin.Current);
                                    if (br.ReadUInt32() != 0x00010000)
                                    {
                                        PrintScriptError("Invalid struct header", f.Position-4);
                                        throw new Exception("Cannot continue decompilation");
                                    }

                                    if (f.Position % 4 != 0)
                                    {
                                        PrintScriptError("Struct is not 4-aligned", f.Position-4);
                                    }

                                    if (padLength > 5)
                                    {
                                        PrintScriptError("Padding unnecessarily long for struct", position+3);
                                    }

                                    // TODO: check alignment of individual struct items.  Not actually necessary for
                                    //       script edits that don't touch struct contents, but would be nice.

                                    br.Seek((int)length - 4, SeekOrigin.Current);
                                    break;
                                case 0x4C:
                                    length = br.ReadUInt32();
                                    if (length == 0)
                                    {
                                        PrintScriptError("Zero-length wide string (no null terminator)", position);
                                        continue;
                                    }
                                    if (length % 2 != 0)
                                    {
                                        PrintScriptError("Invalid string length for wide string", position);
                                    }
                                    br.Seek((int)length-2, SeekOrigin.Current);
                                    if (br.ReadUInt16() != 0)
                                    {
                                        PrintScriptError("Wide string missing null terminator", position);
                                    }
                                    break;
                                default:
                                    if (!IsKnownOpcode(c))
                                    {
                                        PrintScriptError(string.Format("Illegal opcode: {0:X2}", c), position);
                                    }
                                    break;
                            }

                            position = f.Position;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("@@ {0}", ex.Message);
                    errorFoundThisFile = true;
                }

                if (!errorFoundThisFile)
                {
                    Console.WriteLine(">> No errors found");
                }
                Console.WriteLine();
                Console.WriteLine();
            }
            Console.WriteLine("[Press any key to continue]");
            Console.ReadKey();
        }

        private static void CheckEndIfOffset(EndianBinaryReader read_le, string offsetType, long position)
        {
            int offset = read_le.ReadUInt16();
            read_le.Seek(offset - 4, SeekOrigin.Current);
            if (read_le.ReadUInt16() != 0x2801)
            {
                PrintScriptError("Incorrect endif offset in " + offsetType, position);
            }
        }

        private static void CheckIfOffset(EndianBinaryReader read_le, string offsetType, long position)
        {
            int offset = read_le.ReadUInt16();
            if (!IfOffsetIsValid(read_le, offset, position)) {
                PrintScriptError("Incorrect next branch offset in " + offsetType, position);
            }
        }

        private static bool IfOffsetIsValid(EndianBinaryReader read_le, int offset, long position)
        {
            bool b = false;

            read_le.Seek(offset - 6, SeekOrigin.Current);
            b |= (read_le.ReadUInt16() == 0x4801); // points to an else
            b |= (read_le.ReadUInt16() == 0x2801); // points to an endif
            b |= (read_le.Read() == 0x27); // points to an elseif
            read_le.Seek((int)position + 2, SeekOrigin.Begin);
            return b;
        }

        private static void CheckNesting(int nestingLevel, string nestingType, long position) {
            if (nestingLevel < 0)
            {
                PrintScriptError(string.Format("Unbalanced end {0}", nestingType), position);
            }
        }

        private static void PrintScriptError(string message, long position)
        {
            errorFoundThisFile = true;
            Console.WriteLine("## {0:X8}: {1}", position, message);
        }

        private static bool IsKnownOpcode(int code)
        {
            return code == 0x01
                || code >= 0x03 && code <= 0x0F
                || code >= 0x12 && code <= 0x18
                || code == 0x1A || code == 0x1B
                || code >= 0x1E && code <= 0x22
                || code == 0x24
                || code >= 0x27 && code <= 0x29
                || code >= 0x2C && code <= 0x34
                || code == 0x37 && code <= 0x39
                || code >= 0x3C && code <= 0x42
                || code >= 0x47 && code <= 0x4D;
        }
    }
}
