# Disk Image Artifact Parser

A .NET 8 command line parser for raw FAT32 images. It reads the boot sector and FAT, traverses directory clusters, and exports file names, attributes, timestamps, cluster IDs, size, and deleted-entry markers as JSON or CSV. Input images are opened read only.

```sh
dotnet run --project src/DiskArtifacts -- image.img --format json --output artifacts.json
python tests/test_cli.py
```

The parser handles short 8.3 names and ordinary directory chains. Deleted entries are shown with a `?` in place of the removed first character. Long filename entries and file content recovery are outside this parser's scope. It rejects malformed geometry and cyclic directory chains.

