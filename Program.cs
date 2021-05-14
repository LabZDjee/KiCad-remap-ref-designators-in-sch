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
            mapped = new HashSet<int>();
            numberOfParts = 1;
            this.reference = reference;
        }
        public string reference; // value of the new reference designator
        public HashSet<int> mapped; // set containing parts identified
        public int numberOfParts; // number of parts for this reference
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
        [Value(0, MetaName = "<KiCadProjectName>", HelpText = "KiCad project name to work on (without extension).", Required = true)]
        public string KiCadProjectName { get; set; }

        [Option('o', "overwrite", Default = (bool)false, HelpText = "silently overwrites .orgRefMap.sch backup files.", Required = false)]
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
        static Regex mapUCompRx = new Regex(@"^U\s+(\d+)\s+\S+\s+([0-9A-F]+)\s*$", RegexOptions.Compiled);
        static Regex mapARCompRx = new Regex(@"^AR\s+Path\s*=\s*""(\S+)""\s+Ref\s*=\s*""(\S+)""(.*)$", RegexOptions.Compiled);
        static Regex mapF0CompRx = new Regex(@"^F\s*0\s+""(\S+)""(.*)$", RegexOptions.Compiled); // first should be \s+ but it was decided to widen to \s*, just in case

        static List<InMemoryFile> inMemSchBakFiles = new List<InMemoryFile>(); // backup of schematic files
        static List<InMemoryFile> inMemSchFiles = new List<InMemoryFile>(); // schematic files to be altered
        static Dictionary<string, InMemoryFile> sheetPaths = new Dictionary<string, InMemoryFile>(); // paths to schematics files

        static ArgOptions argOptions = null; // command line options

        static void Main(string[] args)
        {
            string applicationName = AppDomain.CurrentDomain.FriendlyName;
            var parser = new CommandLine.Parser(with => with.HelpWriter = null);
            var parserResult = parser.ParseArguments<ArgOptions>(args);
            if (parserResult.Tag == ParserResultType.NotParsed)
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
                ExtractSheets(schFilename, "", inMemSchFiles, sheetPaths);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Exception reading schematics file:\n {e.Message}");
                Exit(-3);
            }
            if (argOptions.Verbose)
            {
                if (inMemSchFiles.Count < 2)
                {
                    Console.WriteLine($"Simple schematics file (no hierarchy detected): {inMemSchFiles[0].fileName}");
                }
                else
                {
                    Console.WriteLine($"{inMemSchFiles.Count} schematic files found in project:");
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
                if (match.Success == false)
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
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = mapEltRx.Match(lines[i]);
                if (match.Success == true)
                {
                    string left = match.Groups[1].Value;
                    string right = match.Groups[2].Value;
                    foreach (string k in map.Keys)
                    {
                        if (k == left)
                        {
                            Console.WriteLine($"Duplication error in \"{mapFilename}\": reference designator \"{left}\" reassigned on line {i + 1}!");
                            Exit(-6);
                        }
                    }
                    foreach (MapValue v in map.Values)
                    {
                        if (v.reference == right)
                        {
                            Console.WriteLine($"Duplication error in \"{mapFilename}\": replacement reference designator \"{right}\" on line {i + 1} already listed!");
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
            var refNotUnique = CheckUniquenessOfReferences(map);
            if (string.IsNullOrEmpty(refNotUnique) == false)
            {
                Console.WriteLine($"Will not proceed: reference designator \"{refNotUnique}\" in schematics is not unique!");
                Exit(-8);
            }
            if (argOptions.Verbose)
            {
                Console.WriteLine("Before proceeding, checked uniqueness of reference designators in schematics: okay");
            }
            // now proceed to the real stuff in memory
            for (int fileIndex = 0; fileIndex < inMemSchFiles.Count; fileIndex++)
            {
                bool inComp = false;
                int hitF = 0, hitL = 0, partNumber = 0;
                string timestamp = "", oldRef = "";
                for (int lineIndex = 0; lineIndex < inMemSchFiles[fileIndex].contents.Length; lineIndex++)
                {
                    string line = inMemSchBakFiles[fileIndex].contents[lineIndex];
                    if (mapCompRx.IsMatch(line))
                    {
                        inComp = true;
                        hitF = 0;
                        hitL = 0;
                    }
                    if (mapEndCompRx.IsMatch(line))
                    {
                        inComp = false;
                        if (hitL > 0)
                        {
                            if (hitF > 0)
                            {
                                if (map[oldRef].mapped.Add(partNumber) == false)
                                {
                                    throw new Exception($"Panic (should never happen)!\n Unexpected part number duplicate {partNumber} for old ref. designator {oldRef}");
                                }
                                if (argOptions.Verbose)
                                {
                                    Console.WriteLine($"Ref. designator \"{oldRef}\" replaced with \"{map[oldRef].reference}\" in L/F0, lines {hitL}/{hitF} of \"{inMemSchFiles[fileIndex].fileName}\"");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"Inconsistency in schematics, cannot proceed:");
                                Console.WriteLine($" reference designator '{oldRef}' just before line {lineIndex + 1} in file {inMemSchFiles[fileIndex].fileName}");
                                Console.WriteLine($" appears in an 'L' line and not in an 'F 0' line");
                                Exit(-9);
                            }
                        }
                    }
                    if (inComp)
                    {
                        Match match = mapUCompRx.Match(line);
                        if (match.Success == true)
                        {
                            timestamp = match.Groups[2].Value;
                            if (int.TryParse(match.Groups[1].Value, out partNumber) == false)
                            {
                                throw new Exception($"Panic (should never happen)!\n When parsing partNumber line {line + 1}, file {inMemSchFiles[fileIndex].fileName}");
                            }
                        }
                        else
                        {
                            match = mapLCompRx.Match(line);
                            if (match.Success == true)
                            {
                                oldRef = match.Groups[2].Value;
                                if (map.ContainsKey(oldRef))
                                {
                                    inMemSchFiles[fileIndex].contents[lineIndex] = $"L {match.Groups[1].Value} {map[oldRef].reference}";
                                    inMemSchFiles[fileIndex].dirty = true;
                                    hitL = lineIndex + 1;
                                }
                            }
                            else
                            {
                                match = mapF0CompRx.Match(line);
                                if (match.Success == true)
                                {
                                    if (hitL > 0 && oldRef != match.Groups[1].Value)
                                    {
                                        Console.WriteLine($"Inconsistency in values of L reference \"{oldRef}\" and F 0 reference \"{match.Groups[1].Value}\"");
                                        Exit(-10);
                                    }
                                    if (map.ContainsKey(oldRef))
                                    {
                                        inMemSchFiles[fileIndex].contents[lineIndex] = $"F 0 \"{map[oldRef].reference}\"{match.Groups[2].Value}";
                                        inMemSchFiles[fileIndex].dirty = true;
                                        hitF = lineIndex + 1;
                                    }
                                }
                                else
                                {
                                    match = mapARCompRx.Match(line);
                                    if (match.Success == true)
                                    {
                                        hitL = hitF = 0;
                                        string path = match.Groups[1].Value;
                                        oldRef = match.Groups[2].Value;
                                        if (map.ContainsKey(oldRef))
                                        {
                                            string newRef = map[oldRef].reference;
                                            inMemSchFiles[fileIndex].contents[lineIndex] = $"AR Path=\"{path}\" Ref=\"{newRef}\"{match.Groups[3].Value}";
                                            if (FindSheetFromArPath(path, timestamp) == inMemSchFiles[fileIndex])
                                            {
                                                if (map[oldRef].mapped.Add(partNumber) == false)
                                                {
                                                    throw new Exception($"Panic (should never happen)!\n Unexpected part number duplicate {partNumber} for old ref. designator {oldRef}");
                                                }
                                                if (argOptions.Verbose)
                                                {
                                                    Console.WriteLine($"Ref. designator \"{oldRef}\" replaced with \"{newRef}\" in AR, line {lineIndex + 1} of file \"{inMemSchFiles[fileIndex].fileName}\"");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            // job completed in memory, now check every substitution was properly ticked as done
            foreach (string oldRef in map.Keys)
            {
                if (CheckConsistencyOfParts(map[oldRef].mapped, map[oldRef].numberOfParts) == false)
                {
                    Console.WriteLine($"Cannot proceed: reference designator '{map[oldRef].reference}' (expected to change from '{oldRef}') not found in schematics");
                    Exit(-11);
                }
            }
            refNotUnique = CheckUniquenessOfReferences(null);
            if (string.IsNullOrEmpty(refNotUnique) == false)
            {
                Console.WriteLine($"Failure after having made replacements: \"{refNotUnique}\" in schematics is not unique!");
                Console.WriteLine($"Probable cause: \"{refNotUnique}\" is defined outside of the list of reference designators to replace");
                Console.WriteLine("This list should be carefully checked");
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

        // given a set containing which parts of a reference designator are defined, checks they
        // contains a contiguous number of values from 1 to partSetCount
        // if partSetCount is negative, it is redefined as number of elements of set
        static bool CheckConsistencyOfParts(HashSet<int> partSet, int partSetCount = -1)
        {
            if (partSetCount < 0)
            {
                partSetCount = partSet.Count;
            }
            if (partSetCount != partSet.Count)
            {
                return false;
            }
            for (int i = 1; i <= partSetCount; i++)
            {
                if (partSet.Contains(i) == false)
                {
                    return false;
                }
            }
            return true;
        }

        // return first duplicated reference designator met if any or null
        // map: if not null and if result is successful, adjusts numberOfParts in this dictionary
        static string CheckUniquenessOfReferences(Dictionary<string, MapValue> map)
        {
            Dictionary<string, HashSet<int>> existingReferences = new Dictionary<string, HashSet<int>>();
            foreach (InMemoryFile f in inMemSchFiles)
            {
                bool insideComp = false;
                bool foundArFields = false;
                string refDesignator = "";
                string timeStamp = "";
                int partNumber = 1;
                foreach (string line in f.contents)
                {
                    if (mapCompRx.Match(line).Success == true)
                    {
                        insideComp = true;
                        foundArFields = false;
                    }
                    else if (mapEndCompRx.Match(line).Success == true)
                    {
                        insideComp = false;
                    }
                    else if (insideComp)
                    {
                        Match match = mapUCompRx.Match(line);
                        if (match.Success == true)
                        {
                            timeStamp = match.Groups[2].Value;
                            if (int.TryParse(match.Groups[1].Value, out partNumber) == false)
                            {
                                throw new Exception($"Panic (unexpected) in CheckUniquenessOfReferences parsing partNumber (line {line + 1}, file {f.fileName})");
                            }
                        }
                        else
                        {
                            refDesignator = "";
                            match = mapARCompRx.Match(line);
                            if (match.Success == true)
                            {
                                foundArFields = true;
                                if (FindSheetFromArPath(match.Groups[1].Value, timeStamp) == f)
                                {
                                    refDesignator = match.Groups[2].Value;
                                }
                            }
                            else if (foundArFields == false)
                            {
                                match = mapF0CompRx.Match(line);
                                if (match.Success == true)
                                {
                                    refDesignator = match.Groups[1].Value;
                                }
                            }
                            if (refDesignator.Length > 0)
                            {
                                if (existingReferences.ContainsKey(refDesignator))
                                {
                                    if (existingReferences[refDesignator].Add(partNumber) == false)
                                    {
                                        return refDesignator;
                                    }
                                }
                                else
                                {
                                    HashSet<int> partSet = new HashSet<int>();
                                    partSet.Add(partNumber);
                                    existingReferences.Add(refDesignator, partSet);
                                }
                            }
                        }
                    }
                }
            }
            foreach (string refDesignator in existingReferences.Keys)
            {
                if (CheckConsistencyOfParts(existingReferences[refDesignator]) == false)
                {
                    return refDesignator;
                }
            }
            if (map != null)
            {
                foreach (string refDesignator in existingReferences.Keys)
                {
                    if (map.ContainsKey(refDesignator))
                    {
                        map[refDesignator].numberOfParts = existingReferences[refDesignator].Count;
                    }
                }
            }
            return null;
        }

        // recursive function which given a schematics filename 'schFilename' adds its filename and contents to 'memFiles' list
        //  and also looks for hierarchical sheets at any level to add them as well to the 'memFiles' list
        static void ExtractSheets(string schFilename, string path, List<InMemoryFile> memFiles, Dictionary<string, InMemoryFile> sheetPaths)
        {
            if (memFiles.Exists(f => f.fileName == schFilename)) // should never happen the way we analyze multiple-page schematic files
            {
                throw new Exception($"Circular reference (or redundant reference) to sheet {schFilename}");
            }
            InMemoryFile memFile = new InMemoryFile(schFilename);
            memFiles.Add(memFile);
            sheetPaths.Add(path, memFile);
            bool inDeepSheet = false;
            string timeStamp = "";
            Regex inSheetRx = new Regex(@"^\$Sheet\s*$", RegexOptions.Compiled);
            Regex outOfSheetRx = new Regex(@"^\$EndSheet\s*$", RegexOptions.Compiled);
            Regex timeStampRx = new Regex(@"^U\s+([0-9A-F]+).*$", RegexOptions.Compiled);
            Regex sheetFilenameRx = new Regex(@"^F1\s+""(\S+)""", RegexOptions.Compiled);
            for (int i = 0; i < memFile.contents.Length; i++)
            {
                if (!inDeepSheet)
                {
                    if (inSheetRx.IsMatch(memFile.contents[i]))
                    {
                        inDeepSheet = true;
                    }
                }
                else
                {
                    if (outOfSheetRx.IsMatch(memFile.contents[i]))
                    {
                        inDeepSheet = false;
                    }
                    else
                    {
                        Match match = timeStampRx.Match(memFile.contents[i]);
                        if (match.Success == true)
                        {
                            timeStamp = match.Groups[1].Value;
                        }
                        else
                        {
                            match = sheetFilenameRx.Match(memFile.contents[i]);
                            if (match.Success == true)
                            {
                                string newPath = $"{path}/{timeStamp}";
                                string newSheetFilename = match.Groups[1].Value;
                                Int32 index = memFiles.FindIndex(f => f.fileName == newSheetFilename);
                                if (index >= 0)
                                {
                                    sheetPaths.Add(newPath, memFiles[index]);
                                }
                                else
                                {
                                    ExtractSheets(newSheetFilename, newPath, memFiles, sheetPaths);
                                }
                            }
                        }
                    }

                }
            }
        }

        // given a full AR path field and a $Comp timestamp (in filed U), return 
        // corresponding InMemoryFile for that sheet or null if path does not designate a sheet
        static InMemoryFile FindSheetFromArPath(string path, string compTimestamp)
        {
            int index = path.IndexOf(compTimestamp);
            if (index >= 0)
            {
                string sheetPath = index > 0 ? path.Remove(index - 1) : "";
                if (sheetPaths.ContainsKey(sheetPath))
                {
                    return sheetPaths[sheetPath];
                }
            }
            return null;
        }

        static void Exit(int code)
        {
            if (argOptions != null)
            {
                if (argOptions.Dryrun == true && code > -1)
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
