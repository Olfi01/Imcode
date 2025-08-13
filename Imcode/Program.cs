using Imcode.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System.CommandLine;
using System.Security.Cryptography;
using System.Text;

namespace Imcode
{
    internal class Program
    {
        private const int MAGIC_NUMBER = -113590060;
        private static PngEncoder pngEncoder = new()
        {
            ColorType = PngColorType.RgbWithAlpha
        };

        static int Main(string[] args)
        {
            RootCommand rootCommand = new("Tool to store and retrieve information in images");

            Command collisionCommand = PrepareCollisionCommand();
            rootCommand.Subcommands.Add(collisionCommand);

            Command encodeCommand = PrepareEncodeCommand();
            rootCommand.Subcommands.Add(encodeCommand);

            Command decodeCommand = PrepareDecodeCommand();
            rootCommand.Subcommands.Add(decodeCommand);

            ParseResult parseResult = rootCommand.Parse(args);
            return parseResult.Invoke();
        }

        private static Command PrepareEncodeCommand()
        {
            Command encodeCommand = new("encode", "Encode given input data into an image file");
            Argument<FileInfo> file = new("imageFile")
            {
                Description = "The path to the file in which to encode the data. This file will be overridden with the modified version unless you specify an output file."
            };
            Argument<string> keyword = new("keyword")
            {
                Description = "The keyword to use for encoding and decoding of data."
            };
            Argument<string> data = new("inputData")
            {
                Description = "Input data as a UTF-8 string. Can also be specified using --in.",
                Arity = ArgumentArity.ZeroOrOne
            };
            Option<FileInfo> inputFile = new("--infile", "--in", "-i")
            {
                Description = "The data to be encoded inside the image file. This will take precedence over inputData if specified."
            };
            Option<FileInfo> outputFile = new("--outfile", "--out", "-o")
            {
                Description = "The path to store the resulting image containing the encoded data to"
            };
            encodeCommand.Arguments.Add(file);
            encodeCommand.Arguments.Add(keyword);
            encodeCommand.Arguments.Add(data);
            encodeCommand.Options.Add(inputFile);
            encodeCommand.Options.Add(outputFile);
            encodeCommand.SetAction(async (parseResult, cancellationToken) =>
                await Encode(parseResult.GetRequiredValue(file), parseResult.GetRequiredValue(keyword), parseResult.GetValue(data), parseResult.GetValue(inputFile), parseResult.GetValue(outputFile), cancellationToken));
            return encodeCommand;
        }

        private static Command PrepareDecodeCommand()
        {
            Command decodeCommand = new("decode", "Decode hidden data from an image file");
            Argument<FileInfo> file = new("imageFile")
            {
                Description = "The path to the file from which to decode the data."
            };
            Argument<string> keyword = new("keyword")
            {
                Description = "The keyword used to encode the file."
            };
            Option<FileInfo> outputFile = new("--outfile", "--out", "-o")
            {
                Description = "The file to output the decoded data to. If not specified, data will be printed to the console, interpreted as a UTF-8 string."
            };
            decodeCommand.Arguments.Add(file);
            decodeCommand.Arguments.Add(keyword);
            decodeCommand.Options.Add(outputFile);
            decodeCommand.SetAction(async (parseResult, cancellationToken) =>
                await Decode(parseResult.GetRequiredValue(file), parseResult.GetRequiredValue(keyword), parseResult.GetValue(outputFile), cancellationToken));
            return decodeCommand;
        }

        private static Command PrepareCollisionCommand()
        {
            Command collisionCommand = new("collision", "Checks if two keywords would use colliding information spaces");
            Argument<string> keyword1Arg = new("keyword1")
            {
                Description = "First keyword"
            };
            Argument<string> keyword2Arg = new("keyword2")
            {
                Description = "Second keyword"
            };
            collisionCommand.Arguments.Add(keyword1Arg);
            collisionCommand.Arguments.Add(keyword2Arg);
            collisionCommand.SetAction(async parseResult => await CheckCollision(parseResult.GetRequiredValue(keyword1Arg), parseResult.GetRequiredValue(keyword2Arg)));
            return collisionCommand;
        }

        private static async Task<int> CheckCollision(string keyword1, string keyword2)
        {
            Random random1 = CreateRandom(keyword1);
            Random random2 = CreateRandom(keyword2);
            int informationSpace1 = random1.Next(16);
            int informationSpace2 = random1.Next(16);
            if (informationSpace1 == informationSpace2)
            {
                await Console.Out.WriteLineAsync($"Uh oh! These two keywords are operating in the same information space ({informationSpace1}) and could possibly collide!");
                return ExitCode.Collision;
            }
            else
            {
                await Console.Out.WriteLineAsync($"These two keywords operate in separate information spaces ({informationSpace1}, {informationSpace2}) and will therefore never collide.");
                return ExitCode.Ok;
            }
        }

        private static Random CreateRandom(string keyword)
        {
            byte[] hashValue = SHA256.HashData(Encoding.UTF8.GetBytes(keyword));
            byte[] seedBytes = new byte[4];
            for (int i = 0; i < hashValue.Length; i++)
            {
                seedBytes[i % 4] = (byte)(seedBytes[i % 4] ^ hashValue[i]);
            }
            int seed = BitConverter.ToInt32(seedBytes);
            return new Random(seed);
        }

        private static async Task<int> Encode(FileInfo imageFile, string keyword, string? inputString, FileInfo? inputFile, FileInfo? outputFile, CancellationToken cancellationToken)
        {
            outputFile ??= imageFile;
            Stream dataStream;
            long dataLength;
            if (inputFile != null)
            {
                dataStream = inputFile.OpenRead();
                dataLength = inputFile.Length;
            }
            else if (inputString != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(inputString);
                dataStream = new MemoryStream(bytes);
                dataLength = bytes.Length;
            }
            else
            {
                await Console.Error.WriteLineAsync("Must specify either inputData or --infile!");
                return ExitCode.InvalidInput;
            }

            long dataCapacity = imageFile.Length / 128;
            if (dataLength > dataCapacity)
            {
                await Console.Error.WriteLineAsync($"Not enough space on image to encode this much data! Maximum amount of data for this image is {dataCapacity} bytes!");
                return ExitCode.InvalidInput;
            }

            Random random = CreateRandom(keyword);
            int informationSpace = random.Next(16);
            int rowOffset = informationSpace >> 2;
            int colOffset = informationSpace & 0b11;
            Dictionary<int, Dictionary<int, byte>> changes = [];
            Image<Rgba32> image;
            using (Stream imageStream = imageFile.OpenRead())
            {
                image = await Image.LoadAsync<Rgba32>(imageStream, cancellationToken);
                using (dataStream)
                {
                    Stack<(int row, int col)> positions = GetShuffledPositions(random, rowOffset, colOffset, image.Height, image.Width);
                    byte[] headerBytes = [.. BitConverter.GetBytes(MAGIC_NUMBER), .. BitConverter.GetBytes(dataLength)];
                    foreach (byte b in headerBytes)
                    {
                        AddByteToChanges(changes, positions, b);
                    }
                    byte[] buffer = new byte[4096];
                    int bytesRead;
                    do
                    {
                        bytesRead = await dataStream.ReadAsync(buffer, cancellationToken);
                        if (bytesRead == 0) break;
                        for (int i = 0; i < bytesRead; i++)
                        {
                            AddByteToChanges(changes, positions, buffer[i]);
                        }
                    } while (true);
                }
                image.ProcessPixelRows(accessor =>
                {
                    for (int row = 0; row < accessor.Height; row++)
                    {
                        if (changes.TryGetValue(row, out Dictionary<int, byte>? rowChanges))
                        {
                            Span<Rgba32> pixelRow = accessor.GetRowSpan(row);
                            foreach (var (col, change) in rowChanges)
                            {
                                ref Rgba32 pixel = ref pixelRow[col];
                                pixel.R = (byte)((pixel.R & 0b11111110) | ((change & 0b1000) >> 3));
                                pixel.G = (byte)((pixel.G & 0b11111110) | ((change & 0b0100) >> 2));
                                pixel.B = (byte)((pixel.B & 0b11111110) | ((change & 0b0010) >> 1));
                                pixel.A = (byte)((pixel.A & 0b11111110) | (change & 0b0001));
                            }
                        }
                    }
                });
            }
            using Stream outputStream = outputFile.OpenWrite();
            await image.SaveAsync(outputStream, pngEncoder, cancellationToken);
            image.Dispose();
            return ExitCode.Ok;
        }

        private static Stack<(int row, int col)> GetShuffledPositions(Random random, int rowOffset, int colOffset, int height, int width)
        {
            List<(int row, int col)> positions = [];
            for (int row = rowOffset; row < height; row += 4)
            {
                for (int col = colOffset; col < width; col += 4)
                {
                    positions.Add((row, col));
                }
            }
            positions.Shuffle(random);
            return new Stack<(int row, int col)>(positions);
        }

        private static void AddByteToChanges(Dictionary<int, Dictionary<int, byte>> changes, Stack<(int row, int col)> positions, byte b)
        {
            byte firstFourBits = (byte)((b & 0b11110000) >> 4);
            var (row, col) = positions.Pop();
            if (changes.TryGetValue(row, out Dictionary<int, byte>? value)) value.Add(col, firstFourBits);
            else changes.Add(row, new Dictionary<int, byte>() { { col, firstFourBits } });
            byte lastFourBits = (byte)(b & 0b00001111);
            (row, col) = positions.Pop();
            if (changes.TryGetValue(row, out value)) value.Add(col, lastFourBits);
            else changes.Add(row, new Dictionary<int, byte>() { { col, lastFourBits } });
        }

        private static async Task<int> Decode(FileInfo imageFile, string keyword, FileInfo? outputFile, CancellationToken cancellationToken)
        {
            Random random = CreateRandom(keyword);
            int informationSpace = random.Next(16);
            int rowOffset = informationSpace >> 2;
            int colOffset = informationSpace & 0b11;
            using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(imageFile.OpenRead(), cancellationToken);
            int dataRows = image.Height / 4;
            int dataCols = image.Width / 4;
            byte[] headerBytes = new byte[12];
            Stack<(int row, int col)> positions = GetShuffledPositions(random, rowOffset, colOffset, image.Height, image.Width);
            for (int i = 0; i < headerBytes.Length; i++)
            {
                headerBytes[i] = GetByte(image, positions);
            }
            int magicNumber = BitConverter.ToInt32(headerBytes.AsSpan()[..4]);
            if (magicNumber != MAGIC_NUMBER)
            {
                await Console.Error.WriteLineAsync("It seems like either there is no data here to decode or you have entered the wrong keyword.");
                return ExitCode.InvalidInput;
            }
            long dataLength = BitConverter.ToInt64(headerBytes.AsSpan()[4..]);
            Stream outputStream;
            if (outputFile == null)
            {
                outputStream = new MemoryStream();
            }
            else
            {
                outputStream = outputFile.OpenWrite();
            }
            using (outputStream)
            {
                long bytesLeft = dataLength;
                byte[] buffer = new byte[4096];
                while (bytesLeft > 0)
                {
                    int bytesToRead = (int)Math.Min(bytesLeft, buffer.Length);
                    for (int i = 0; i < bytesToRead; i++)
                    {
                        buffer[i] = GetByte(image, positions);
                    }
                    await outputStream.WriteAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
                    bytesLeft -= bytesToRead;
                }
            }
            if (outputFile == null && (outputStream is MemoryStream memoryStream))
            {
                await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(memoryStream.ToArray()));
            }
            return ExitCode.Ok;
        }

        private static byte GetByte(Image<Rgba32> image, Stack<(int row, int col)> positions)
        {
            var (row, col) = positions.Pop();
            Rgba32 pixel = image[col, row];
            byte firstFourBits = (byte)(((pixel.R & 1) << 3)
                | ((pixel.G & 1) << 2)
                | ((pixel.B & 1) << 1)
                | (pixel.A & 1));
            (row, col) = positions.Pop();
            pixel = image[col, row];
            byte lastFourBits = (byte)(((pixel.R & 1) << 3)
                | ((pixel.G & 1) << 2)
                | ((pixel.B & 1) << 1)
                | (pixel.A & 1));
            return (byte)((firstFourBits << 4) | lastFourBits);
        }
    }
}
