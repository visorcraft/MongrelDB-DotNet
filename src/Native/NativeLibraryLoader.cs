using System.Reflection;
using System.Runtime.InteropServices;

namespace Visorcraft.MongrelDB.Native;

/// <summary>
/// Resolves the native library load path for libmongreldb and libmongreldb_kit.
/// Checks the MONGRELDB_NATIVE_DIR env var first, then the NuGet-provided
/// runtimes/&lt;rid&gt;/native/ path, then the default system search path.
/// </summary>
internal static class NativeLibraryLoader
{
	private static int _initialized;

	/// <summary>
	/// Registers the DllImport resolvers for both libraries. Safe to call
	/// multiple times; only the first call takes effect.
	/// </summary>
	public static void EnsureInitialized()
	{
		if (Interlocked.Exchange(ref _initialized, 1) != 0)
			return;

		// Both interop classes are in the same assembly. SetDllImportResolver
		// can only be called once per assembly, so we register a single
		// resolver that handles both library names.
		var asm = typeof(MongrelDBInterop).Assembly;
		try
		{
			NativeLibrary.SetDllImportResolver(asm, ResolveLibrary);
		}
		catch (InvalidOperationException)
		{
			// Already registered (e.g. by a previous call from the same assembly).
		}
	}

	private static IntPtr ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
	{
		IntPtr h;

		// 1. MONGRELDB_NATIVE_DIR env var (user-supplied path to the .so/.dylib/.dll)
		var envDir = Environment.GetEnvironmentVariable("MONGRELDB_NATIVE_DIR");
		if (!string.IsNullOrEmpty(envDir))
		{
			var path = Path.Combine(envDir, FileName(libraryName));
			if (NativeLibrary.TryLoad(path, out h))
				return h;
		}

		// 2. runtimes/<rid>/native/ alongside the assembly (NuGet package layout)
		var rid = RuntimeIdentifier;
		if (!string.IsNullOrEmpty(rid))
		{
			var baseDir = AppContext.BaseDirectory;
			var runtimePath = Path.Combine(baseDir, "runtimes", rid, "native", FileName(libraryName));
			if (NativeLibrary.TryLoad(runtimePath, out h))
				return h;
		}

		// 3. Default system search path (LD_LIBRARY_PATH, DYLD_LIBRARY_PATH, PATH)
		if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out h))
			return h;

		return IntPtr.Zero;
	}

	private static string FileName(string libName)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return libName + ".dll";
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			return "lib" + libName + ".dylib";
		return "lib" + libName + ".so";
	}

	private static string RuntimeIdentifier
	{
		get
		{
			var arch = RuntimeInformation.OSArchitecture switch
			{
				Architecture.X64 => "x64",
				Architecture.Arm64 => "arm64",
				Architecture.X86 => "x86",
				_ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
			};

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				// musl detection: check if the process is statically linked or
				// if MUSL is in the libc path. Most .NET Linux builds are glibc;
				// musl users can override via MONGRELDB_NATIVE_DIR.
				return IsMusl() ? $"linux-musl-{arch}" : $"linux-{arch}";
			}
			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
				return $"osx-{arch}";
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				return $"win-{arch}";
			return "";
		}
	}

	private static bool IsMusl()
	{
		try
		{
			// /proc/self/maps will mention "ld-musl" on musl-based systems
			var maps = File.ReadAllText("/proc/self/maps");
			return maps.Contains("ld-musl", StringComparison.OrdinalIgnoreCase);
		}
		catch
		{
			return false;
		}
	}
}
