using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Tesseract;

namespace VideoMaskingTool
{
    internal class AnalyzedFrame
    {
        public string? Framepath { get; set; }
        public string Filename { get; set; } = string.Empty;
        public List<Rect>? Rects { get; set; }
    }

    internal class Program
    {
        // const string INPUT_VIDEO_PATH = "test_input.mp4";
        const string INPUT_VIDEO_PATH = "test_input.webm";

        //const string OUTPUT_VIDEO_PATH = "output.mp4";
        const string OUTPUT_VIDEO_PATH = "output.webm";

        const int OUTPUT_FRAMERATE = 3;

        const string INPUT_FRAMES_DIR = "input_frames";
        const string OUTPUT_FRAMES_DIR = "output_frames";
        const string WORD_TO_MASK = "User";

        static async Task Main(string[] args)
        {
            var preparationStopwatch = new Stopwatch();
            var videoSplittingStopwatch = new Stopwatch();
            var framesAnalyzingStopwatch = new Stopwatch();
            var framesProcessingStopwatch = new Stopwatch();
            var framesMergingStopwatch = new Stopwatch();

            preparationStopwatch.Start();
            var inputFramesDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), INPUT_FRAMES_DIR);
            if (Directory.Exists(inputFramesDirectoryPath))
            {
                Directory.Delete(inputFramesDirectoryPath, true);
            }
            Directory.CreateDirectory(inputFramesDirectoryPath);

            var outputFramesDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), OUTPUT_FRAMES_DIR);
            if (Directory.Exists(outputFramesDirectoryPath))
            {
                Directory.Delete(outputFramesDirectoryPath, true);
            }
            Directory.CreateDirectory(outputFramesDirectoryPath);

            var outputDirectoryPath = Path.Combine(Directory.GetCurrentDirectory(), OUTPUT_VIDEO_PATH);
            if (File.Exists(outputDirectoryPath))
            {
                File.Delete(outputDirectoryPath);
            }
            preparationStopwatch.Stop();

            // splitting video into frames
            videoSplittingStopwatch.Start();
            Console.WriteLine("Splitting video into frames...");
            var splitIntoFramesCliCommand = GetVideoSplitCommand(INPUT_VIDEO_PATH, INPUT_FRAMES_DIR);
            ExecuteFfmpegCli(splitIntoFramesCliCommand);
            videoSplittingStopwatch.Stop();



            // analyzing frames
            framesAnalyzingStopwatch.Start();
            Console.WriteLine("Analyzing frames...");

            var analyzedFrames = new ConcurrentBag<AnalyzedFrame>();
            var inputFrames2 = Directory.EnumerateFiles(INPUT_FRAMES_DIR);

            var tasks2 = new List<Task>();
            foreach (var frame2 in inputFrames2)
            {
                tasks2.Add(Task.Run(() =>
                {
                    analyzedFrames.Add(new AnalyzedFrame
                    {
                        Framepath = frame2,
                        Filename = Path.GetFileName(frame2),
                        Rects = GetRectsToMask(frame2, WORD_TO_MASK)
                    });
                }));
            }
            await Task.WhenAll(tasks2);
            framesAnalyzingStopwatch.Stop();


            // masking frames
            framesProcessingStopwatch.Start();
            Console.WriteLine("Masking frames...");
            var tasks = new List<Task>();
            foreach (var frame in analyzedFrames)
            {
                tasks.Add(Task.Run(() => MaskFrame(frame)));
            }
            await Task.WhenAll(tasks);
            framesProcessingStopwatch.Stop();


            // merging masked frames
            framesMergingStopwatch.Start();
            Console.WriteLine("Merging frames...");
            var framesMergeCommand = BuildFramesMergeCommand(OUTPUT_FRAMES_DIR, OUTPUT_VIDEO_PATH);
            ExecuteFfmpegCli(framesMergeCommand);
            framesMergingStopwatch.Stop();




            Console.WriteLine("\n\n\n");
            Console.WriteLine("Done!");
            Console.WriteLine("-------------------------------------------------------");
            Console.WriteLine($"Prep: \t{preparationStopwatch.Elapsed}");
            Console.WriteLine($"Splt: \t{videoSplittingStopwatch.Elapsed}");
            Console.WriteLine($"Anal: \t{framesAnalyzingStopwatch.Elapsed}");
            Console.WriteLine($"Proc: \t{framesProcessingStopwatch.Elapsed}");
            Console.WriteLine($"Merg: \t{framesMergingStopwatch.Elapsed}");
            Console.WriteLine("-------------------------------------------------------");
        }

        private static void MaskFrame(AnalyzedFrame frame)
        {
            if (frame.Rects?.Count > 0)
            {
                var ffmpegCliBlurCommand = BuildBlurCommand(frame.Rects, frame.Framepath, Path.Combine(OUTPUT_FRAMES_DIR, frame.Filename));
                ExecuteFfmpegCli(ffmpegCliBlurCommand);
            }
        }

        private static void MaskFrame(string inputFrame)
        {
            var filename = Path.GetFileName(inputFrame);
            var rectsToMask = GetRectsToMask(inputFrame, WORD_TO_MASK);

            if (rectsToMask.Count > 0)
            {
                var ffmpegCliBlurCommand = BuildBlurCommand(rectsToMask, inputFrame, Path.Combine(OUTPUT_FRAMES_DIR, filename));
                ExecuteFfmpegCli(ffmpegCliBlurCommand);
            }
        }

        private static string GetVideoSplitCommand(string inputFramePath, string outputDirPath)
        {
            return $"-i {inputFramePath} ./{outputDirPath}/frame_%04d.png";
        }
        private static string BuildFramesMergeCommand(string inputDir, string outputPath)
        {
            return $"-r 3 -i ./{inputDir}/frame_%04d.png -r 3 {outputPath}";
        }

        private static List<Rect> GetRectsToMask(string inputFramePath, string searchPattern)
        {
            var rects = new List<Rect>();
            try
            {
                using var engine = new TesseractEngine(@"./", "eng", EngineMode.Default);
                engine.SetVariable("user_defined_dpi", "131");
                engine.SetVariable("debug_file", "/dev/null");
                using var img = Pix.LoadFromFile(inputFramePath);
                using var page = engine.Process(img);
                using var iter = page.GetIterator();
                iter.Begin();

                do
                {
                    if (iter.TryGetBoundingBox(PageIteratorLevel.Word, out var rect))
                    {
                        if (iter.GetText(PageIteratorLevel.Word).Contains(searchPattern))
                        {
                            rects.Add(rect);
                        }
                    }
                } while (iter.Next(PageIteratorLevel.Word));
            }
            catch (Exception e)
            {
                Console.WriteLine("Unexpected Error: " + e.Message);
            }
            return rects;
        }

        private static string BuildBlurCommand(List<Rect> rects, string? inputFramePath, string? outputPath)
        {
            if(inputFramePath == null || outputPath == null)
            {
                throw new Exception("Input/output frame path not provided while building blur command");
            }

            var sb = new StringBuilder();
            sb.Append("-hide_banner -loglevel error ");
            sb.Append($"-i {inputFramePath} -filter_complex \"");
            sb.Append($"split={rects.Count}");

            for (int i = 0; i < rects.Count; i++)
            {
                sb.Append($"[blur{i}]");
            }
            sb.Append($";");

            for (int i = 0; i < rects.Count; i++)
            {
                var rect = rects[i];
                sb.Append($"[blur{i}]boxblur=5:1[cropped{i}];");
                sb.Append($"[cropped{i}]crop={rect.Width + 10}:{rect.Height + 10}:{rect.X1}:{rect.Y1}[blurred{i}];");
            }

            for (int i = 0; i < rects.Count; i++)
            {
                var rect = rects[i];

                if (i == 0)
                {
                    sb.Append($"[0:v][blurred{i}]overlay={rect.X1}:{rect.Y1}[bg{i}]");

                    if (rects.Count > 0)
                    {
                        sb.Append(";");
                    }

                    continue;
                }

                if (i < rects.Count - 1)
                {
                    sb.Append($"[bg{i - 1}][blurred{i}]overlay={rect.X1}:{rect.Y1}[bg{i}];");
                    continue;
                }

                if (i == rects.Count - 1)
                {
                    sb.Append($"[bg{i - 1}][blurred{i}]overlay={rect.X1}:{rect.Y1}");
                    continue;
                }
            }

            sb.Append($"\" ");
            sb.Append(outputPath);

            return sb.ToString();
        }

        private static void ExecuteFfmpegCli(string command)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = command,
                // RedirectStandardError = true,
            };

            using var process = Process.Start(processInfo);
            process?.WaitForExit();
            process?.Kill();
        }
    }
}