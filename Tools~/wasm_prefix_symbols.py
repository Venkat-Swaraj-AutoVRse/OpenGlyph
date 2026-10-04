#!/usr/bin/env python3
"""Prefix every bundled-library symbol in the WebGL (wasm) static archive.

Unity's Web player (WebGL 2 and WebGPU) links its own FreeType, HarfBuzz and libpng into
TextRenderingModule / libpng.a. OpenGlyph's self-contained libunitext_native.a carries its own
copies of the same libraries, so linking both fails with ~1400 "wasm-ld: error: duplicate symbol"
errors (FT_*, hb_*, png_*, C++ OT:: internals). The upstream archive avoided this by renaming every
internal symbol to __ut_<name>; this script does the same for an archive built by the CI workflow.

Every symbol DEFINED by an object of the archive is renamed to PREFIX + name, except the public
ut_* entry points the C# P/Invoke layer calls. References to those symbols from other members are
renamed consistently; references to symbols the archive does not define (libc, emscripten runtime)
are left alone. COMDAT group names are prefixed too so C++ inline functions are never merged with
Unity's (different-version) copies.

Only the "linking" custom section (symbol table, comdat names) and import names of undefined
symbols change; relocations refer to symbols by index, so code and data are untouched.

usage: wasm_prefix_symbols.py <llvm-ar> <in.a> <out.a> [--prefix __ut_] [--keep ut_]
"""
import argparse
import os
import shutil
import subprocess
import sys
import tempfile

WASM_MAGIC = b"\0asm"

# linking section subsection ids
WASM_SEGMENT_INFO = 5
WASM_INIT_FUNCS = 6
WASM_COMDAT_INFO = 7
WASM_SYMBOL_TABLE = 8

# symbol kinds
SYM_FUNCTION, SYM_DATA, SYM_GLOBAL, SYM_SECTION, SYM_TAG, SYM_TABLE = 0, 1, 2, 3, 4, 5

# symbol flags
FLAG_BINDING_LOCAL = 0x2
FLAG_UNDEFINED = 0x10
FLAG_EXPLICIT_NAME = 0x40

# import kinds
IMP_FUNC, IMP_TABLE, IMP_MEMORY, IMP_GLOBAL, IMP_TAG = 0, 1, 2, 3, 4


def read_uleb(b, p):
    result = shift = 0
    while True:
        byte = b[p]
        p += 1
        result |= (byte & 0x7F) << shift
        shift += 7
        if not byte & 0x80:
            return result, p


def write_uleb(v):
    out = bytearray()
    while True:
        byte = v & 0x7F
        v >>= 7
        if v:
            out.append(byte | 0x80)
        else:
            out.append(byte)
            return bytes(out)


def read_str(b, p):
    n, p = read_uleb(b, p)
    return b[p:p + n].decode("utf-8"), p + n


def write_str(s):
    e = s.encode("utf-8")
    return write_uleb(len(e)) + e


def read_limits(b, p):
    flags, p = read_uleb(b, p)
    _, p = read_uleb(b, p)
    if flags & 1:
        _, p = read_uleb(b, p)
    return p


def sections(data):
    if data[:4] != WASM_MAGIC:
        raise ValueError("not a wasm object")
    p = 8
    out = []
    while p < len(data):
        sid = data[p]
        size, q = read_uleb(data, p + 1)
        out.append((sid, data[q:q + size]))
        p = q + size
    return out


class Obj:
    """One wasm object: its sections plus the parsed import list and symbol table."""

    def __init__(self, data):
        self.secs = sections(data)
        self.imports = []  # [kind, module, field, raw descriptor bytes]
        self.func_imports = []  # import list index of each imported function, in order
        self.global_imports, self.table_imports, self.tag_imports = [], [], []
        for sid, body in self.secs:
            if sid == 2:
                self._parse_imports(body)
        self.symbols = None

    def _parse_imports(self, body):
        n, p = read_uleb(body, 0)
        for _ in range(n):
            mod, p = read_str(body, p)
            field, p = read_str(body, p)
            kind = body[p]
            start = p
            p += 1
            if kind == IMP_FUNC:
                _, p = read_uleb(body, p)
                self.func_imports.append(len(self.imports))
            elif kind == IMP_TABLE:
                p += 1
                p = read_limits(body, p)
                self.table_imports.append(len(self.imports))
            elif kind == IMP_MEMORY:
                p = read_limits(body, p)
            elif kind == IMP_GLOBAL:
                p += 2
                self.global_imports.append(len(self.imports))
            elif kind == IMP_TAG:
                p += 1
                _, p = read_uleb(body, p)
                self.tag_imports.append(len(self.imports))
            else:
                raise ValueError("unknown import kind %d" % kind)
            self.imports.append([kind, mod, field, body[start:p]])

    def _imports_for(self, kind):
        return {SYM_FUNCTION: self.func_imports, SYM_GLOBAL: self.global_imports,
                SYM_TABLE: self.table_imports, SYM_TAG: self.tag_imports}[kind]

    def linking(self):
        for i, (sid, body) in enumerate(self.secs):
            if sid == 0:
                name, p = read_str(body, 0)
                if name == "linking":
                    return i, body, p
        return None, None, None

    def parse_symbols(self):
        """Returns [(kind, flags, name, raw-tail-after-name-or-None, index)]"""
        _, body, p = self.linking()
        syms = []
        if body is None:
            self.symbols = syms
            return syms
        _, p = read_uleb(body, p)  # version
        while p < len(body):
            sub = body[p]
            size, q = read_uleb(body, p + 1)
            payload = body[q:q + size]
            p = q + size
            if sub != WASM_SYMBOL_TABLE:
                continue
            n, r = read_uleb(payload, 0)
            for _ in range(n):
                kind = payload[r]
                flags, r = read_uleb(payload, r + 1)
                if kind in (SYM_FUNCTION, SYM_GLOBAL, SYM_TAG, SYM_TABLE):
                    idx, r = read_uleb(payload, r)
                    if not flags & FLAG_UNDEFINED or flags & FLAG_EXPLICIT_NAME:
                        name, r = read_str(payload, r)
                    else:
                        imp = self.imports[self._imports_for(kind)[idx]]
                        name = imp[2]
                    syms.append((kind, flags, name, idx))
                elif kind == SYM_DATA:
                    name, r = read_str(payload, r)
                    if not flags & FLAG_UNDEFINED:
                        _, r = read_uleb(payload, r)
                        _, r = read_uleb(payload, r)
                        _, r = read_uleb(payload, r)
                    syms.append((kind, flags, name, None))
                elif kind == SYM_SECTION:
                    _, r = read_uleb(payload, r)
                    syms.append((kind, flags, None, None))
                else:
                    raise ValueError("unknown symbol kind %d" % kind)
        self.symbols = syms
        return syms

    def rewrite(self, rename):
        """Returns new object bytes with every symbol in `rename` (old -> new) renamed."""
        li, body, p0 = self.linking()
        # Rename import fields of undefined symbols that take their name from the import.
        renamed_imports = {}
        new_secs = list(self.secs)
        out_link = bytearray(body[:p0])
        version, p = read_uleb(body, p0)
        out_link += write_uleb(version)
        while p < len(body):
            sub = body[p]
            size, q = read_uleb(body, p + 1)
            payload = body[q:q + size]
            p = q + size
            if sub == WASM_SYMBOL_TABLE:
                payload = self._rewrite_symtab(payload, rename, renamed_imports)
            elif sub == WASM_COMDAT_INFO:
                payload = self._rewrite_comdats(payload, rename)
            out_link.append(sub)
            out_link += write_uleb(len(payload)) + payload
        new_secs[li] = (0, bytes(out_link))
        if renamed_imports:
            for i, (sid, b) in enumerate(new_secs):
                if sid == 2:
                    new_secs[i] = (2, self._rewrite_imports(renamed_imports))
        out = bytearray(WASM_MAGIC + b"\x01\0\0\0")
        for sid, b in new_secs:
            out.append(sid)
            out += write_uleb(len(b)) + b
        return bytes(out)

    def _rewrite_symtab(self, payload, rename, renamed_imports):
        n, r = read_uleb(payload, 0)
        out = bytearray(write_uleb(n))
        for _ in range(n):
            start = r
            kind = payload[r]
            flags, r = read_uleb(payload, r + 1)
            if kind in (SYM_FUNCTION, SYM_GLOBAL, SYM_TAG, SYM_TABLE):
                idx, r = read_uleb(payload, r)
                if not flags & FLAG_UNDEFINED or flags & FLAG_EXPLICIT_NAME:
                    name, r = read_str(payload, r)
                    new = rename.get(name, name) if not flags & FLAG_BINDING_LOCAL else name
                    out.append(kind)
                    out += write_uleb(flags) + write_uleb(idx) + write_str(new)
                else:
                    imp_i = self._imports_for(kind)[idx]
                    name = self.imports[imp_i][2]
                    if name in rename:
                        renamed_imports[imp_i] = rename[name]
                    out += payload[start:r]
            elif kind == SYM_DATA:
                name, r = read_str(payload, r)
                tail_start = r
                if not flags & FLAG_UNDEFINED:
                    _, r = read_uleb(payload, r)
                    _, r = read_uleb(payload, r)
                    _, r = read_uleb(payload, r)
                new = rename.get(name, name) if not flags & FLAG_BINDING_LOCAL else name
                out.append(kind)
                out += write_uleb(flags) + write_str(new) + payload[tail_start:r]
            elif kind == SYM_SECTION:
                _, r = read_uleb(payload, r)
                out += payload[start:r]
        return bytes(out)

    @staticmethod
    def _rewrite_comdats(payload, rename):
        n, r = read_uleb(payload, 0)
        out = bytearray(write_uleb(n))
        for _ in range(n):
            name, r = read_str(payload, r)
            flags, r = read_uleb(payload, r)
            cnt, r = read_uleb(payload, r)
            entries_start = r
            for _ in range(cnt):
                r += 1  # kind
                _, r = read_uleb(payload, r)
            out += write_str(rename.get(name, name)) + write_uleb(flags) + write_uleb(cnt) + payload[entries_start:r]
        return bytes(out)

    def _rewrite_imports(self, renamed_imports):
        out = bytearray(write_uleb(len(self.imports)))
        for i, (kind, mod, field, desc) in enumerate(self.imports):
            out += write_str(mod) + write_str(renamed_imports.get(i, field)) + desc
        return bytes(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("llvm_ar")
    ap.add_argument("inp")
    ap.add_argument("out")
    ap.add_argument("--prefix", default="__ut_")
    ap.add_argument("--keep", default="ut_", help="defined symbols starting with this stay public")
    a = ap.parse_args()

    work = tempfile.mkdtemp(prefix="wasmprefix_")
    try:
        members = subprocess.run([a.llvm_ar, "t", a.inp], check=True, capture_output=True, text=True).stdout.split()
        if len(set(members)) != len(members):
            raise SystemExit("duplicate member names in archive; extraction would clobber objects")
        subprocess.run([a.llvm_ar, "x", os.path.abspath(a.inp)], check=True, cwd=work)
        objs = {}
        for m in members:
            with open(os.path.join(work, m), "rb") as f:
                data = f.read()
            if data[:4] != WASM_MAGIC:
                continue
            o = Obj(data)
            o.parse_symbols()
            objs[m] = o

        defined = set()
        for o in objs.values():
            for kind, flags, name, _ in o.symbols:
                if name and not flags & FLAG_UNDEFINED and not flags & FLAG_BINDING_LOCAL:
                    defined.add(name)
        rename = {n: a.prefix + n for n in defined if not n.startswith(a.keep) and not n.startswith(a.prefix)}
        kept = sorted(n for n in defined if n.startswith(a.keep))
        print("objects: %d, defined globals: %d, renamed: %d, kept public: %d"
              % (len(objs), len(defined), len(rename), len(kept)))

        for m, o in objs.items():
            with open(os.path.join(work, m), "wb") as f:
                f.write(o.rewrite(rename))

        tmp_out = os.path.join(work, "out.a")
        subprocess.run([a.llvm_ar, "rcs", tmp_out] + members, check=True, cwd=work)
        shutil.copyfile(tmp_out, a.out)
        print("wrote", a.out)
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
