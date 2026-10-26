"""Local, single-link Windows IO. No optional dependencies or RVC imports."""
import contextlib
import ctypes
from ctypes import wintypes
import os
from pathlib import Path
import re
import stat


class ProtocolError(ValueError):
    def __init__(self):
        super().__init__("voxcpm_training.worker.invalid_protocol")


def require(condition):
    if not condition:
        raise ProtocolError()


def io_path(path):
    value = os.fspath(path)
    require(isinstance(value, str) and 0 < len(value) <= 32700 and not any(ord(c) < 32 for c in value))
    if os.name == "nt":
        logical = value[4:] if value.startswith("\\\\?\\") else value
        require(re.match(r"^[A-Za-z]:[\\/]", logical) is not None)
        require(not any(part in (".", "..") for part in re.split(r"[\\/]", logical)[1:]))
        for part in re.split(r"[\\/]", logical)[1:]:
            if part:
                require(part[-1] not in (".", " ") and not any(c in ':<>"|?*' for c in part))
                require(part.split(".")[0].upper() not in {"CON", "PRN", "AUX", "NUL", *("COM" + str(i) for i in range(1, 10)), *("LPT" + str(i) for i in range(1, 10))})
        return Path("\\\\?\\" + logical.replace("/", "\\"))
    require(value.startswith("/") and not any(p in (".", "..") for p in value.split("/")))
    return Path(value)


if os.name == "nt":
    class FileInfo(ctypes.Structure):
        _fields_ = [("attributes", wintypes.DWORD), ("creation", wintypes.FILETIME),
                    ("access", wintypes.FILETIME), ("write", wintypes.FILETIME),
                    ("volume", wintypes.DWORD), ("sizeHigh", wintypes.DWORD),
                    ("sizeLow", wintypes.DWORD), ("links", wintypes.DWORD),
                    ("indexHigh", wintypes.DWORD), ("indexLow", wintypes.DWORD)]

    _kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    _kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    _kernel.CreateFileW.restype = wintypes.HANDLE
    _kernel.GetFileInformationByHandle.argtypes = [wintypes.HANDLE, ctypes.POINTER(FileInfo)]
    _kernel.GetFileInformationByHandle.restype = wintypes.BOOL
    _kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    _kernel.CloseHandle.restype = wintypes.BOOL


def _open(path, directory=False, create=False, mutable=False):
    path = io_path(path)
    if os.name != "nt":
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
        if create:
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
        fd = os.open(path, flags, 0o600)
        try:
            info = os.fstat(fd)
            require(stat.S_ISDIR(info.st_mode) if directory else stat.S_ISREG(info.st_mode) and info.st_nlink == 1)
            return fd
        except BaseException:
            os.close(fd)
            raise
    access = 1 if directory else (0x40000000 if create else 0x80000000)
    share = 3 if directory or mutable else 1
    handle = _kernel.CreateFileW(str(path), access, share, None, 1 if create else 3,
                                 0x00200000 | (0x02000000 if directory else 0), None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        info = FileInfo()
        if not _kernel.GetFileInformationByHandle(handle, ctypes.byref(info)):
            raise ctypes.WinError(ctypes.get_last_error())
        require(not info.attributes & 0x400 and bool(info.attributes & 0x10) == directory)
        require(directory or info.links == 1)
        if directory:
            result, handle = handle, None
            return result
        import msvcrt
        fd = msvcrt.open_osfhandle(handle, os.O_BINARY | (os.O_WRONLY if create else os.O_RDONLY))
        handle = None
        return fd
    finally:
        if handle is not None:
            _kernel.CloseHandle(handle)


@contextlib.contextmanager
def pin_directories(path):
    path = io_path(path)
    handles = []
    try:
        for item in reversed((path, *path.parents)):
            handles.append(_open(item, directory=True))
        yield path
    finally:
        for handle in reversed(handles):
            if os.name == "nt":
                _kernel.CloseHandle(handle)
            else:
                os.close(handle)


@contextlib.contextmanager
def open_file(path, create=False, mutable=False):
    path = io_path(path)
    with pin_directories(path.parent):
        fd = _open(path, create=create, mutable=mutable)
        with os.fdopen(fd, "wb" if create else "rb", buffering=0) as stream:
            yield stream


def exists_plain_file(path):
    try:
        with open_file(path):
            return True
    except FileNotFoundError:
        return False
