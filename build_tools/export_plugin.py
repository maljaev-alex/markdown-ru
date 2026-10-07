"""Export Notepad++'s six C ABI entry points without requiring .NET 2.0 MSBuild.

Uses Microsoft's ILDasm and the .NET Framework ILAsm already present on Windows.
The explicit entry-point set is checked so upstream API changes fail the build.
"""
import argparse
import ctypes
from pathlib import Path
import re
import shutil
import subprocess
import tempfile


def export_plugin(dll, ildasm, ilasm, work):
    dll = Path(dll).resolve()
    with tempfile.TemporaryDirectory(prefix="export-", dir=work) as directory:
        folder = Path(directory)
        il = folder / "plugin.il"
        subprocess.run([str(ildasm), str(dll), "/utf8", "/out=" + str(il)], cwd=folder, check=True)
        text = il.read_text(encoding="utf-8-sig")
        entrypoints = ["isUnicode", "setInfo", "getFuncsArray", "messageProc", "getName", "beNotified"]
        for ordinal, name in enumerate(entrypoints, 1):
            pattern = r"(\.method private hidebysig static\s+)(bool|void|native int|uint32)(\s+)" + name + r"(\([^{}]*?\) cil managed\s*\{)"
            replacement = (r"\g<1>\g<2> modopt([mscorlib]System.Runtime.CompilerServices.CallConvCdecl) "
                           + name + r"\g<4>\n    .export [" + str(ordinal) + "] as " + name)
            text, count = re.subn(pattern, replacement, text)
            if count != 1:
                raise RuntimeError("Expected exactly one ABI entry point: " + name)
        text, count = re.subn(r"\.corflags\s+0x[0-9A-Fa-f]+", ".corflags 0x00000000", text)
        if count != 1:
            raise RuntimeError("Missing CLR header flags")
        il.write_text(text, encoding="utf-8-sig")
        result = folder / dll.name
        resources = il.with_suffix(".res")
        arguments = [str(ilasm), str(il), "/dll", "/x64", "/quiet", "/output=" + str(result)]
        if resources.exists():
            arguments.append("/resource=" + str(resources))
        subprocess.run(arguments, cwd=folder, check=True)
        shutil.copy2(result, dll)
        library = ctypes.WinDLL(str(dll))
        for name in entrypoints:
            getattr(library, name)
        library.isUnicode.restype = ctypes.c_int
        library.getName.restype = ctypes.c_void_p
        if library.isUnicode() != 1 or ctypes.wstring_at(library.getName()) != "AnotherMarkdown + Translate-RU":
            raise RuntimeError("Native plugin ABI smoke test failed")
        print("Exported x64 Notepad++ ABI:", ", ".join(entrypoints))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("dll")
    parser.add_argument("--ildasm", required=True)
    parser.add_argument("--ilasm", required=True)
    parser.add_argument("--work", required=True)
    args = parser.parse_args()
    export_plugin(args.dll, args.ildasm, args.ilasm, args.work)
