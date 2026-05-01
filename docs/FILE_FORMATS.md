# File Formats and I/O

Floss supports importing industry-standard formats and has its own native format for lossless serialization.

## Krita (`.kra`) Importer

The Krita importer is located in `KraImporter.cs`. Krita files are essentially ZIP archives containing XML metadata (like `maindoc.xml`) and raw tiled binary pixel data.

- The importer unpacks the archive in-memory.
- It parses the XML to reconstruct the layer hierarchy, including group layers, opacity, visibility, and blend modes.
- It maps Krita blend modes to Floss equivalents.
- Tile data is decompressed (often LZF or DEFLATE) and converted into `TiledPixelBuffer` format, allowing large documents to be imported without allocating a single monolithic array.

## Photoshop Document (`.psd`) Importer

The PSD importer is located in the `Psd/` directory.

- PSDs are highly structured binary files. The importer reads the file header, color mode data, image resources, and layer/mask information blocks.
- It extracts layers and their pixel data.
- Note: The importer handles standard RGB files well, but advanced features like adjustment layers or vector smart objects are either skipped or rasterized.

## Native Floss Format (`.floss`)

The native file format is handled by `FlossFileFormat.cs`.

- **Structure**: It's a binary format using a magic header, versioning, and compressed chunks.
- **Serialization**: It writes the document properties (width, height, paper color), then recursively serializes the layer tree.
- **Tile Compression**: Layer pixel tiles are compressed using Deflate/Brotli to minimize file size, preserving the exact `TiledPixelBuffer` state.

## Image Formats

`ImageFileFormat.cs` provides basic support for standard image formats:
- Uses Avalonia's built-in imaging libraries or SkiaSharp directly to load and export `.png`, `.jpg`, and `.bmp`.
- A flattened image import results in a single-layer document.
- Exporting composites the layer stack against the paper color (unless transparent PNG is requested) and writes it out.
