namespace Mandrill.Core.IO;

/// <summary>File-system error categories; the UI maps each to an i18n key (architecture §10).</summary>
public enum FsErrorKind
{
    /// <summary>File, folder or path does not exist.</summary>
    NotFound,

    /// <summary>Access denied.</summary>
    AccessDenied,

    /// <summary>The path names a file where a folder was expected.</summary>
    NotADirectory,

    /// <summary>The device is not ready (e.g. empty card reader).</summary>
    NotReady,

    /// <summary>The text is not a valid path or name.</summary>
    InvalidPath,

    /// <summary>The target name already exists.</summary>
    AlreadyExists,

    /// <summary>The file or folder is in use.</summary>
    InUse,

    /// <summary>The path is too long for the API that was used.</summary>
    PathTooLong,

    /// <summary>Not supported yet (e.g. UNC paths in Phase 1).</summary>
    Unsupported,

    /// <summary>Anything else.</summary>
    Other,

    /// <summary>No application is associated with the file type (ERROR_NO_ASSOCIATION; AC-1.27).</summary>
    NoAssociation,
}

/// <summary>An error that crosses threads as a value, never as an exception (architecture §2).</summary>
/// <param name="Kind">Category.</param>
/// <param name="Path">Path the operation was about.</param>
/// <param name="Win32Code">Underlying Win32 error code, or 0 when unknown.</param>
public sealed record FsError(FsErrorKind Kind, string Path, int Win32Code);

/// <summary>
/// Result of a file operation: a value or an <see cref="FsError"/>. Create it with <see cref="Success"/> or
/// <see cref="Failure"/>; <c>default</c> reads as a success with a null value.
/// </summary>
/// <typeparam name="T">Value type.</typeparam>
public readonly record struct FsResult<T>
{
    private FsResult(T? value, FsError? error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>The value when <see cref="Succeeded"/>.</summary>
    public T? Value { get; }

    /// <summary>The error when the operation failed.</summary>
    public FsError? Error { get; }

    /// <summary>True when there is no error.</summary>
    public bool Succeeded => Error is null;

    /// <summary>Creates a successful result.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static FsResult<T> Success(T value) => new(value, null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The error.</param>
    /// <returns>The result.</returns>
    public static FsResult<T> Failure(FsError error) => new(default, error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>Maps exceptions and Win32 codes to <see cref="FsError"/> (architecture §10).</summary>
public static class FsErrors
{
    /// <summary>Maps a Win32 error code.</summary>
    /// <param name="win32Code">Win32 error code.</param>
    /// <returns>The category.</returns>
    public static FsErrorKind KindOf(int win32Code) => win32Code switch
    {
        2 or 3 or 15 => FsErrorKind.NotFound,           // FILE/PATH_NOT_FOUND, INVALID_DRIVE
        5 => FsErrorKind.AccessDenied,
        21 => FsErrorKind.NotReady,
        267 => FsErrorKind.NotADirectory,               // ERROR_DIRECTORY
        32 or 33 => FsErrorKind.InUse,                  // SHARING/LOCK_VIOLATION
        80 or 183 => FsErrorKind.AlreadyExists,
        206 => FsErrorKind.PathTooLong,                 // FILENAME_EXCED_RANGE
        123 or 161 => FsErrorKind.InvalidPath,          // INVALID_NAME, BAD_PATHNAME
        1155 => FsErrorKind.NoAssociation,              // ERROR_NO_ASSOCIATION
        _ => FsErrorKind.Other,
    };

    /// <summary>Maps an exception from a file-system call.</summary>
    /// <param name="ex">The exception.</param>
    /// <param name="path">The path the call was about.</param>
    /// <returns>The error value.</returns>
    public static FsError FromException(Exception ex, string path)
    {
        ArgumentNullException.ThrowIfNull(ex);
        // Only HRESULTs from the Win32 facility (0x8007xxxx) carry a Win32 code; COR_E_IO and friends do not.
        int code = (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? ex.HResult & 0xFFFF : 0;
        return ex switch
        {
            UnauthorizedAccessException => new FsError(FsErrorKind.AccessDenied, path, 5),
            PathTooLongException => new FsError(FsErrorKind.PathTooLong, path, 206),
            DirectoryNotFoundException => new FsError(FsErrorKind.NotFound, path, code is 0 ? 3 : code),
            FileNotFoundException => new FsError(FsErrorKind.NotFound, path, code is 0 ? 2 : code),
            IOException => new FsError(KindOf(code), path, code),
            ArgumentException or NotSupportedException => new FsError(FsErrorKind.InvalidPath, path, 123),
            _ => new FsError(FsErrorKind.Other, path, 0),
        };
    }
}
