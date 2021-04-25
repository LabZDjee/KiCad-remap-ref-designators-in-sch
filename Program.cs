using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CommandLine;
using CommandLine.Text;

namespace SwapRefsInSch
{
    // used to store a new reference designator
    class MapValue
    {
        public MapValue(string reference)
        {
            mapped = false;
            this.reference = reference;
        }
        public string reference; // value of the reference designator
        public bool mapped; // true when already found
    }

    // stores contents of a file in memory together with its name
    class InMemoryFile
    {
        public InMemoryFile(string fileName) // this constructor attempts to fill it from disk
        {
            this.fileName = fileName;
            dirty = false;
            contents = File.ReadAllLines(fileName);
        }
        public InMemoryFile(string fileName, string[] contents) // this constructor allows to clone contents
        {
            this.fileName = fileName;
            dirty = false;
            this.contents = (string[])contents.Clone();
        }
        public void WriteBack() // to disk
        {
            File.WriteAllLines(fileName, contents);
        }
        public string fileName;
        public bool dirty;
        public string[] contents;
    }

    class ArgOptions
    {
        [Value(0, MetaName = "<KiCadProjectName>",HelpText ="KiCad project name to work on (without extension).", Required = true)]
        public string KiCadProjectName { get; set; }

        [Option('o', "overwrite", Default = (bool)false,HelpText = "silently overwrites .orgRefMap.sch backup files.", Required = false )]
        public bool OverwriteBackup { get; set; }

        [Option('d', "dryrun", Default = (bool)false, HelpText = "dry run: only works in memory. Nothing written to disk.", Required = false)]
        public bool Dryrun { get; set; }

        [Option('v', "verbose", Default = (bool)false, HelpText = "provides details about the process. Otherwise remains silent as long as everything goes right.", Required = false)]
        public bool Verbose { get; set; }

    }

    class Program
    {
        // regular expressions useful to capture a component and its reference designator
        static Regex mapCompRx = new Regex(@"^\$Comp\s*$", RegexOptions.Compiled);
        static Regex mapEndCompRx = new Regex(@"^\$EndComp\s*$", RegexOptions.Compiled);
        static Regex mapLCompRx = new Regex(@"^L\s+(\S+)\s+(\S+)\s*$", RegexOptions.Compiled);
        static Regex mapF0CompRx = new Regex(@"^F\s*0\s+""(\S+)""(.*)$", RegexOptions.Compiled); // first should be \s+ but it was decided to widden to \s*, just in case

        static List<InMemoryFile> inMemSchBakFiles = new List<InMemoryFile>(); // backup of schematic files
        static List<InMemoryFile> inMemSchFiles = new List<InMemoryFile>(); // schematic files to be altered

        static ArgOptions argOptions = null; // command line options

        static void Main(string[] args)
        {
            string applicationName = AppDomain.CurrentDomain.FriendlyName;
            var parser = new CommandLine.Parser(with => with.HelpWriter = null);
            var parserResult = parser.ParseArguments<ArgOptions>(args);
            if(parserResult.Tag == ParserResultType.NotParsed)
            {
                string helpText = HelpText.AutoBuild(parserResult, h =>
                {
                    h.AdditionalNewLineAfterOption = false; //remove newline between options
                    h.Heading = $"{applicationName} <KiCadProjectName> [<options>]\n\n" +
                                 "Takes a <KiCadProjectName>.sch file and a <KiCadProjectName>.refRemap file,\n" +
                                 "creates <KiCadProjectName>.orgRefMap.sch backup file, and\n" +
                                 "alters <KiCadProjectName>.sch with reference designators remapped\n" +
                                 "<KiCadSchFile>.refRemap defines changes in reference designators, one by line like this:\n" +
                                 " <oldRef>\\t<newRef>\n" +
                                 "Note: in case of hierarchical .sch files all could be affected (and backed up)\n" +
                                 "Source: https://github.com/LabZDjee/KiCad-remap-ref-designators-in-sch";
                    h.Copyright = "Copyright (c) 2021 LabZDjee";
                    return h;
                }, e => e);
                Console.WriteLine(helpText.Replace("(pos. 0)", "        "));
                Exit(-1);
            }
            Parsed<ArgOptions> parsed = (Parsed<ArgOptions>)parserResult;
            argOptions = parsed.Value;
            string schFilename = $"{argOptions.KiCadProjectName}.sch";
            string mapFilename = $"{argOptions.KiCadProjectName}.refRemap";
            if (argOptions.Verbose)
            {
                Console.WriteLine($"Proceeds with map file: '{mapFilename}'");
                Console.WriteLine($"Parameters: --dryrun {argOptions.Dryrun} --overwrite {argOptions.OverwriteBackup} --verbose {argOptions.Verbose}");
            }
            if (!File.Exists(schFilename))
            {
                Console.WriteLine($"Cannot proceed: {schFilename} not found!");
                Exit(-2);
            }
            try
            {
                ExtractSheets(schFilename, inMemSchFiles);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception reading schematics file:\n {e.Message}");
                Exit(-3);
            }
            if (argOptions.Verbose)
            {
                if(inMemSchFiles.Count < 2)
                {
                    Console.WriteLine($"Simple schematics file (no hierarchy detected): {inMemSchFiles[0].fileName}");
                }
                else
                {
                    Console.WriteLine($"{inMemSchFiles.Count} schematics files found in hierarchy:");
                    foreach (InMemoryFile f in inMemSchFiles)
                    {
                        Console.WriteLine($" {f.fileName}");
                    }
                }
            }
            Regex extractSchRx = new Regex(@"(.*)\.(sch)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            foreach (InMemoryFile f in inMemSchFiles)
            {
                Match match = extractSchRx.Match(f.fileName);
                if(match.Success == false)
                {
                    throw new Exception($"Panic: unexpected failure trying to extract .sch from file name '{f.fileName}'");
                }
                string backupFilename = $"{match.Groups[1]}.orgRefMap.{match.Groups[2]}";
                if (File.Exists(backupFilename) && !argOptions.OverwriteBackup)
                {
                    Console.WriteLine($"Cannot proceed: {backupFilename} already exists! Erase it first!");
                    Exit(-4);
                }
                inMemSchBakFiles.Add(new InMemoryFile(backupFilename, f.contents));
            }
            if (!File.Exists(mapFilename))
            {
               Console.WriteLine($"Cannot proceed: {mapFilename} not found!");
               Exit(-5);
            }
            string[] lines = File.ReadAllLines(mapFilename);
            Dictionary<string, MapValue> map = new Dictionary<string, MapValue>(); // associative array: key is old reference designator, value contains new
            Regex mapEltRx = new Regex(@"(\S+)\s+(\S+)", RegexOptions.Compiled);
            // checks if reference designators are unique, done separately in both lists
            for (int i=0; i<lines.Length; i++)
            {
              Match match = mapEltRx.Match(lines[i]);
              if(match.Success == true)
                {
                    string left = match.Groups[1].Value;
                    string right = match.Groups[2].Value;
                    foreach (string k in map.Keys)
                    {
                        if (k == left)
                        {
                            Console.WriteLine($"Error: reference designator \"{left}\" reassigned on line {i + 1} duplicated!");
                            Exit(-6);
                        }
                    }
                    foreach (MapValue v in map.Values)
                    {
                        if(v.reference==right)
                        {
                            Console.WriteLine($"Error: replacement reference designator \"{right}\" on line {i + 1} already listed!");
                            Exit(-7);
                        }
                    }
                    map.Add(left, new MapValue(right));
                }
            }
            if (argOptions.Verbose)
            {
                Console.WriteLine("Mapping file checked okay to proceed (no duplicate in old and new list of reference designators)");
            }
            var refNotUnique = checkUniquenessOfReferences();
            if(string.IsNullOrEmpty(refNotUnique) == false)
            {
                Console.WriteLine($"Will not proceed: reference designator \"{refNotUnique}\" in schematics is not unique!");
                Exit(-8);
            }
            if (argOptions.Verbose)
            {
                Console.WriteLine("Before proceeding, checked uniqueness of reference designators in schematics: okay");
            }
            // now proceed to the real stuff in memory
            foreach (string oldRef in map.Keys)
            {
                for (int fileIndex=0; fileIndex< inMemSchFiles.Count; fileIndex++)
                {
                    bool inComp = false, hitF = false, hitL = false;
                    for (int lineIndex = 0; lineIndex < inMemSchFiles[fileIndex].contents.Length; lineIndex++)
                    {
                        string line = inMemSchBakFiles[fileIndex].contents[lineIndex];
                        if (mapCompRx.IsMatch(line))
                        {
                            inComp = true;
                            hitF = false;
                        }
                        if (mapEndCompRx.IsMatch(line))
                        {
                            inComp = false;
                            if(hitF && !hitL)
                            {
                                Console.WriteLine($"Inconsistency in schematics, cannot proceed:");
                                Console.WriteLine($" reference designator '{oldRef}' just before line {lineIndex + 1} in file {inMemSchFiles[fileIndex].fileName}");
                                Console.WriteLine($" appears in an 'L' line and not in an 'F 0' line");
                                Exit(-9);
                            }
                        }
                        if (inComp)
                        {
                            Match match = mapLCompRx.Match(line);
                            if(match.Success == true && match.Groups[2].Value == oldRef)
                            {
                                if(map[oldRef].mapped == true)
                                {
                                    Console.WriteLine($"Inconsistency in schematics, cannot proceed:");
                                    Console.WriteLine($" reference designator '{oldRef}' at line {lineIndex+1} in file {inMemSchFiles[fileIndex].fileName} already met previously");
                                    Exit(-10);
                                }
                                inMemSchFiles[fileIndex].contents[lineIndex] = $"L {match.Groups[1].Value} {map[oldRef].reference}";
                                inMemSchFiles[fileIndex].dirty = true;
                                hitF = true;
                                MapValue mapVal = map[oldRef];
                                mapVal.mapped = true;
                            }
                            if(hitF)
                            {
                               match = mapF0CompRx.Match(line);
                               if(match.Success == true && match.Groups[1].Value == oldRef)
                                {
                                    hitL = true;
                                    inMemSchFiles[fileIndex].contents[lineIndex] = $"F 0 \"{map[oldRef].reference}\"{match.Groups[2].Value}";
                                    if (argOptions.Verbose)
                                    {
                                        Console.WriteLine($" - Replaced {oldRef} with {map[oldRef].reference} in {inMemSchFiles[fileIndex].fileName}");
                                    }
                                }
                            }
                        }

                    }
                }
            }
            // job completed in memory, now check every substitution was properly ticked as done
            foreach(string oldRef in map.Keys)
            {
                if(map[oldRef].mapped == false)
                {
                    Console.WriteLine($"Cannot proceed: reference designator '{map[oldRef].reference}' (expected to change from '{oldRef}') not found in schematics");
                    Exit(-11);
                }
            }
            refNotUnique = checkUniquenessOfReferences();
            if (string.IsNullOrEmpty(refNotUnique) == false)
            {
                Console.WriteLine($"Failure after having made replacements \"{refNotUnique}\" in schematics is not unique!");
                Exit(-12);
            }
            if (argOptions.Verbose)
            {
                Console.WriteLine("In-memory process successful");
                Console.WriteLine("All applied and uniqueness of reference designators after remapping is okay");
            }
            // all seems consistent at this stage: write all stuff to disk (if not in a dry-run test)
            if (argOptions.Dryrun == false)
            {
                for (int indexFile = 0; indexFile < inMemSchFiles.Count; indexFile++)
                {
                    if (inMemSchFiles[indexFile].dirty)
                    {
                        inMemSchFiles[indexFile].WriteBack();
                        inMemSchBakFiles[indexFile].WriteBack();
                        if (argOptions.Verbose)
                        {
                            Console.WriteLine($"Files ${inMemSchFiles[indexFile].fileName} and ${inMemSchBakFiles[indexFile].fileName} written to disk");
                        }
                    }
                }
            }
            if (argOptions.Verbose)
            {
                Console.WriteLine("Process complete, exits");
            }
            Exit(0);
        }

        // return first duplicate met if any or null
        static string checkUniquenessOfReferences()
        {
            Dictionary<string, bool> existingReferences = new Dictionary<string, bool>();
            foreach(InMemoryFile f in inMemSchFiles)
            {
                bool insideComp = false;
                foreach(string line in f.contents)
                {
                    if(mapCompRx.Match(line).Success == true)
                    {
                        insideComp = true;
                    } else if(mapEndCompRx.Match(line).Success == true)
                    {
                        insideComp = false;
                    } else if (insideComp)
                    {
                        Match match = mapLCompRx.Match(line);
                        if(match.Success == true)
                        {
                            string referenceDesignator = match.Groups[2].Value;
                            if (existingReferences.ContainsKey(referenceDesignator))
                            {
                                return referenceDesignator;
                            }
                            existingReferences.Add(referenceDesignator, true);
                        }
                    }
                }
            }
            return null;
        }
        // recursive function which given a schematics filename 'schFilename' adds its filename and contents to 'memFiles' list
        //  and also looks for hierarchical sheets at any level to add them as well to the 'memFiles' list
        static void ExtractSheets(string schFilename, List<InMemoryFile> memFiles)
        {
            if(memFiles.Exists(f => f.fileName == schFilename))
            {
                throw new Exception($"Circular reference (or redundant reference) to sheet {schFilename}");
            }
            InMemoryFile memFile = new InMemoryFile(schFilename);
            memFiles.Add(memFile);
            bool inDeepSheet = false;
            Regex inSheetRx = new Regex(@"^\$Sheet\s*$", RegexOptions.Compiled);
            Regex outOfSheetRx = new Regex(@"^\$EndSheet\s*$", RegexOptions.Compiled);
            Regex sheetFilenameRx = new Regex(@"^F1\s+""(\S+)""", RegexOptions.Compiled);
            for (int i = 0; i< memFile.contents.Length; i++)
            {
                if(!inDeepSheet)
                {
                    if (inSheetRx.IsMatch(memFile.contents[i]))
                    {
                        inDeepSheet = true;
                    }
                } else
                {
                    if (outOfSheetRx.IsMatch(memFile.contents[i]))
                    {
                        inDeepSheet = false;
                    } else
                    {
                        Match match = sheetFilenameRx.Match(memFile.contents[i]);
                        if(match.Success == true)
                        {
                            ExtractSheets(match.Groups[1].Value, memFiles);
                        }
                    }

                }
            }
        }

        static void Exit(int code)
        {
            if(argOptions != null)
            {
                if (argOptions.Dryrun == true)
                {
                    Console.WriteLine("Only a dry-run: no file was created or altered in any way");
                }
                if (argOptions.Verbose)
                {
                    Console.WriteLine($"Return code: {code}");
                }
            }
            System.Environment.Exit(code);
        }
    }
}
