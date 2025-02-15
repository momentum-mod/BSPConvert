namespace BSPConvert.Lib;

using System;
using System.IO.Compression;
using LibBSP;

public class ContentManager : IDisposable
{
    public string ContentDir { get; private set; }

    public BSP[] BSPFiles { get; private set; }

    private static readonly string Q3CONTENT_FOLDER = "Q3Content";

    public ContentManager(string inputFile)
    {
        if (!File.Exists(inputFile))
            throw new FileNotFoundException(inputFile);

        CreateContentDir(inputFile);
        LoadBSPFiles(inputFile);
    }

    // Create a temp directory used for converting assets across engines
    private void CreateContentDir(string inputFile)
    {
        string fileName = Path.GetFileNameWithoutExtension(inputFile);
        ContentDir = Path.Combine(Path.GetTempPath(), fileName);

        // Delete any pre-existing temp content directory
        if (Directory.Exists(ContentDir))
            Directory.Delete(ContentDir, true);

        Directory.CreateDirectory(ContentDir);
    }

    private void LoadBSPFiles(string inputFile)
    {
        string ext = Path.GetExtension(inputFile);
        if (ext == ".bsp")
            BSPFiles = [new BSP(new FileInfo(inputFile))];
        else if (ext == ".pk3")
        {
            // Extract bsp's from pk3 archive
            ZipFile.ExtractToDirectory(inputFile, ContentDir);

            string[] files = Directory.GetFiles(ContentDir, "*.bsp", SearchOption.AllDirectories);
            BSPFiles = new BSP[files.Length];
            for (int i = 0; i < files.Length; i++)
                BSPFiles[i] = new BSP(new FileInfo(files[i]));
        }
        else
            throw new Exception("Invalid input file extension: " + ext);
    }

    public static string GetQ3ContentDir() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Q3CONTENT_FOLDER);

    public void Dispose()
    {
        // Delete temp content directory
        if (Directory.Exists(ContentDir))
            Directory.Delete(ContentDir, true);
    }
}
