using System.Collections.Concurrent;

namespace PhotoCleaner
{
    internal class Program
    {
        public static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Please provide a directory path as an argument.");
                return 1;
            }

            string directoryPath = args[0];
            if (!Directory.Exists(directoryPath))
            {
                Console.WriteLine($"Directory not found: '{directoryPath}'");
                return 1;
            }

            Program program = new();
            return await program.Execute(directoryPath);
        }

        private readonly ConcurrentBag<string> _fileNameBag = [];
        private readonly ConcurrentBag<string> _unknownExtensionBag = [];
        const int _threadCount = 2;

        private async Task<int> Execute(string directoryPath)
        {
            int failedCount = 0;
            try
            {
                Console.WriteLine($"Enumerating files in '{directoryPath}' ...");

                // Get all files in root directory
                DirectoryInfo rootDir = new(directoryPath);
                foreach (FileInfo file in rootDir.GetFiles("*", SearchOption.TopDirectoryOnly))
                {
                    _fileNameBag.Add(file.FullName);
                }

                // Get all top level directories
                DirectoryInfo[] topLevelDirs = rootDir.GetDirectories();
                topLevelDirs
                    .AsParallel()
                    .WithDegreeOfParallelism(_threadCount)
                    .ForAll(dir =>
                    {
                        // Get all files in each directory
                        foreach (FileInfo file in dir.GetFiles("*", SearchOption.AllDirectories))
                        {
                            _fileNameBag.Add(file.FullName);
                        }
                    });

                // Process files in parallel
                Console.WriteLine($"Processing {_fileNameBag.Count} files ...");
                _fileNameBag
                    .AsParallel()
                    .WithDegreeOfParallelism(_threadCount)
                    .ForAll(async fileName =>
                    {
                        ProcessTask processTask = new(
                            _fileNameBag,
                            _unknownExtensionBag,
                            new FileInfo(fileName)
                        );
                        if (!await processTask.Execute())
                        {
                            Interlocked.Increment(ref failedCount);
                        }
                    });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                return 1;
            }
            Console.WriteLine("");
            Console.WriteLine("Processing complete.");

            if (_unknownExtensionBag.Count > 0)
            {
                List<string> unknownExtensionList = _unknownExtensionBag.ToList();
                unknownExtensionList.Sort();
                Console.WriteLine("");
                Console.WriteLine("Unknown extensions:");
                foreach (string extension in unknownExtensionList)
                {
                    Console.WriteLine($"'{extension}'");
                }
            }

            if (failedCount > 0)
            {
                Console.WriteLine("");
                Console.WriteLine($"Potential problem files: {failedCount}");
            }

            return 0;
        }
    }
}
