"""Windows read pins: reject reparse points and deny replacement while in use."""
from contextlib import ExitStack, contextmanager
import io
import os
from pathlib import Path
import stat


def plain_path(path: Path) -> Path:
    path = Path(path)
    if not path.is_absolute() or ".." in path.parts or len(path.parts) < 2:
        raise OSError("invalid owned path")
    if os.name == "nt":
        if len(path.drive) != 2 or path.drive[1] != ":":
            raise OSError("local drive required")
        reserved = {"CON", "PRN", "AUX", "NUL"} | {
            f"{prefix}{n}" for prefix in ("COM", "LPT") for n in range(1, 10)}
        for component in path.parts[1:]:
            if (component.endswith((".", " ")) or ":" in component
                    or any(ord(c) < 32 for c in component)
                    or component.split(".")[0].upper() in reserved):
                raise OSError("invalid owned path")
    return path


def reject_link(path: Path, directory: bool):
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
        raise OSError("reparse point")
    if (directory and not stat.S_ISDIR(info.st_mode)) or (not directory and not stat.S_ISREG(info.st_mode)):
        raise OSError("wrong file type")
    if not directory and info.st_nlink != 1:
        raise OSError("multiply linked file")


if os.name == "nt":
    import ctypes
    from ctypes import wintypes
    import msvcrt

    kernel = ctypes.WinDLL("kernel32", use_last_error=True)

    class FileInfo(ctypes.Structure):
        _fields_ = [("attributes", wintypes.DWORD), ("creation", wintypes.FILETIME),
                    ("access", wintypes.FILETIME), ("write", wintypes.FILETIME),
                    ("volume", wintypes.DWORD), ("size_high", wintypes.DWORD),
                    ("size_low", wintypes.DWORD), ("links", wintypes.DWORD),
                    ("index_high", wintypes.DWORD), ("index_low", wintypes.DWORD)]

    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                  ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.GetFileInformationByHandle.argtypes = [wintypes.HANDLE, ctypes.POINTER(FileInfo)]
    kernel.GetFileInformationByHandle.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL

    @contextmanager
    def pin(path: Path, directory: bool):
        # OPEN_REPARSE_POINT inspects the entry itself, not a reparse destination.
        # Directory pins allow writes but not delete/rename; file pins deny both.
        # FILE_LIST_DIRECTORY participates in sharing enforcement; a metadata-
        # only FILE_READ_ATTRIBUTES handle does not reliably block rename.
        handle = kernel.CreateFileW(str(path), 1 if directory else 0x80000000,
                                    3 if directory else 1, None, 3,
                                    0x00200000 | (0x02000000 if directory else 0), None)
        if handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            info = FileInfo()
            if not kernel.GetFileInformationByHandle(handle, ctypes.byref(info)):
                raise ctypes.WinError(ctypes.get_last_error())
            if info.attributes & 0x400 or bool(info.attributes & 0x10) != directory:
                raise OSError("reparse or wrong type")
            if not directory and info.links != 1:
                raise OSError("multiply linked file")
            if directory:
                yield None
            else:
                fd = msvcrt.open_osfhandle(handle, os.O_RDONLY | os.O_BINARY)
                handle = None  # descriptor now owns the native handle
                with os.fdopen(fd, "rb") as stream:
                    yield stream
        finally:
            if handle is not None and not kernel.CloseHandle(handle):
                raise ctypes.WinError(ctypes.get_last_error())
else:
    @contextmanager
    def pin(path: Path, directory: bool):
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
        if directory:
            flags |= os.O_DIRECTORY
        fd = os.open(path, flags)
        try:
            info = os.fstat(fd)
            if directory:
                if not stat.S_ISDIR(info.st_mode):
                    raise OSError("wrong type")
                yield None
            else:
                if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
                    raise OSError("wrong type")
                with os.fdopen(fd, "rb", closefd=False) as stream:
                    yield stream
        finally:
            os.close(fd)


def pin_chain(stack: ExitStack, directory: Path):
    plain_path(directory)
    for ancestor in (*reversed(directory.parents), directory):
        reject_link(ancestor, True)
        stack.enter_context(pin(ancestor, True))


@contextmanager
def pinned_file(path: Path):
    with ExitStack() as stack:
        pin_chain(stack, path.parent)
        reject_link(path, False)
        yield stack.enter_context(pin(path, False))
