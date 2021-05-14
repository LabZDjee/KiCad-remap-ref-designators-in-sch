# KiCad-remap-ref-designators-in-sch

An attempt to make manual back-annotation from PCB to schematic in [KiCad](https://www.kicad.org/) 5 easier and safer

Current version: **1.2**

## Foreword

With KiCad (version 5.1.6, as of writing), it seems there is no way to rename [reference designator](https://en.wikipedia.org/wiki/Reference_designator)s in PCB for an more logical localization of them on the PCB

It also exists some tools which reorganize reference designators automatically (horizontally then vertically on the PCB or the other way around) and re-synchronize PCB and schematics. Those tools are quite old not reliable and suffer from another limitation according to some needs: they affect all the components and for some like connectors this is not always desirable

The idea of a manual reorganization of reference designators came as not very fast but more reliable: user establishes a list of reference designator suitable replacements scrutinizing the PCB, then applying those in the schematic with the command line utility presented here. Synchronization in the PCB is done by KiCad

## Work-flow

The order of tasks this program proposes is:

- Locate on the PCB, which reference designators need being reorganized

- And in a text file (or in a worksheet which will be saved as tab separated values), write lines with two values separated by a tab character: on the left the existing reference designator to be replaced and on the right the new reference designator

- Present command line program will take this text file and *patch* the `.sch` schematics file(s)

- Then synchronization of PCB file done by KiCad with *Tools* / *Update PCB from Schematic* opting with default Match Method: *Keep existing symbol to footprint association*

  ![update-PCB-from-schematic](update-PCB-from-schematic.png)

## How To

This is a command line program (default name is `KcdMapRefsInSc`)

Just launch it with a single parameter with the KidCad project name without any extension: let call it `KiCadProjectName` for sake of the example

At least two files are supposed to exist: `KiCadProjectName.sch` and `KiCadProjectName.refMap`, first being *Eeschema* schematic file and the other a text file defining replacements, one definition per line, each definition composed of a tab-separated pair of reference designators existing in the schematics followed by the new reference designators

Program will check consistency in those files: no duplicates in schematic file before and after replacements, no duplicates in old and new reference designators listed in `.refMap`, all old reference designators exist in schematic file

When this done `KiCadProjectName.sch` is rewritten and a backup file is created: `KiCadProjectName.orgRefMap.sch`. Note: if backup file already existed, process will be aborted

In case of multiple-page schematic files, process will also apply on `.sch` schematic subfiles. Note that only altered `.sch` files will have a, `.orgRefMap.sch` backup file created

Any error or failure during verifications will result in an error message displayed and no file to be written to disk

When this is done, as already explained, it is up the user to update its PCB from the schematic file

### Program Options

- `--overwrite` will proceed even if `orgRefMap.sch` backup file already existed
- `--dryrun` will run the entire process, however nothing will be written to disk
- `--verbose` normally as long as process runs without error, no display is output. With this option every processing step is displayed


### Limitations and Technical Details

As KiCad versions 6+ uses [S-expression](https://en.wikipedia.org/wiki/S-expression)s in file formats which are completely different from KiCad versions up to 5, this utility will not work with versions 6+

Multiple-page and hierarchical schematic files are supported and all altered schematic file required by the process will be altered

The process is governed by `$Sheet` entries found in the main/project schematic file. In any schematic file, reference designators are supposed to be defined by a `$Comp` entry in the `L` field and also repeated on its `F 0` field

Moreover with multiple-page and hierarchical schematic files, more complex definitions exist involving `AR` fields within those `$Comp` definitions. This new syntax is now supported although it was not described in the  old *Kidcad_file_format.pdf* included in this project as a reference document and retrieved [here](https://dev-docs.kicad.org/en/file-formats/legacy_file_format_documentation.pdf)

Replacement are rather aggressive: all `L`, `F 0`, and `AR` fields have their reference designators replaced whenever found in the replacement list. However, in order to check consistency in the process, what is considered a valid reference designator is governed by those rules:

- `AR` fields are considered valid when their path attribute refers to the sheet they are defined in and the correct component timestamp (which is not always the case: sometimes after copy-paste operations, some paths refer to sheets which no longer exist or are different from the sheet those `AR` fields are defined in)
- `AR` fields when existing invalidates any information found in `L` and `F 0` fields
- In the absence of any `AR` field, `L` and `F 0` fields are considered and they must contain the same reference designator

## Source Code

Source code is written in *C# 5+* and was developed under *Visual Studio 2019* as a *Console Application* for *.Net Framework* 

Two dependencies are necessary for building this project:

- [CommandLineParser](https://github.com/commandlineparser/commandline) which manages command line parameters and options
- [ILMerge](https://github.com/dotnet/ILMerge) which merges output executable file and `.dll` file made by *CommandLineParser* into a single  executable file, store into `/bin` folder (automatically as a Post-build script when compiling project as *Release*)

## Binary for Windows

A binary for *Windows*, with at least `.Net` runtime at version 4 installed, is stored in `/bin` folder under name `KcdMapRefsInSc.exe`. It works out of the box



