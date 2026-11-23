"""Enable Unity legacy VR (OpenVR) in a Unity 2018.4 player build.

Rewrites BuildSettings.enabledVRDevices inside globalgamemanagers
(SerializedFile format 17, type trees stripped). Field order of BuildSettings
was read from the type-tree strings in UnityPlayer.dll:
scenes, preloadedPlugins, enabledVRDevices, buildTags, buildGUID, ...

usage:
  python ggm_vr_patch.py inspect <globalgamemanagers>
  python ggm_vr_patch.py patch   <globalgamemanagers> None,OpenVR
  python ggm_vr_patch.py restore <globalgamemanagers>
"""
import os
import shutil
import struct
import sys

BACKUP_SUFFIX = ".cloudpunkvr-orig"
BUILD_SETTINGS = 141


class Reader:
    def __init__(self, data, pos=0):
        self.d = data
        self.p = pos

    def u8(self):
        v = self.d[self.p]
        self.p += 1
        return v

    def i16(self):
        v = struct.unpack_from("<h", self.d, self.p)[0]
        self.p += 2
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.d, self.p)[0]
        self.p += 4
        return v

    def u32(self):
        v = struct.unpack_from("<I", self.d, self.p)[0]
        self.p += 4
        return v

    def i64(self):
        v = struct.unpack_from("<q", self.d, self.p)[0]
        self.p += 8
        return v

    def cstr(self):
        end = self.d.index(b"\0", self.p)
        s = self.d[self.p:end].decode("utf-8")
        self.p = end + 1
        return s

    def align(self, n=4):
        self.p = (self.p + n - 1) // n * n

    def astr(self):
        n = self.i32()
        s = self.d[self.p:self.p + n].decode("utf-8")
        self.p += n
        self.align()
        return s

    def str_list(self):
        return [self.astr() for _ in range(self.i32())]


def parse(data):
    meta_size, file_size, version, data_offset = struct.unpack_from(">IIII", data, 0)
    if version != 17:
        raise SystemExit(f"unsupported SerializedFile version {version} (expected 17 / Unity 2018.4)")
    if data[16] != 0:
        raise SystemExit("big-endian files are not supported")
    r = Reader(data, 20)
    unity_version = r.cstr()
    r.i32()  # target platform
    type_tree = r.u8()
    if type_tree:
        raise SystemExit("file has type trees; this tool only handles stripped player builds")
    types = []
    for _ in range(r.i32()):
        class_id = r.i32()
        r.u8()  # isStrippedType
        r.i16()  # scriptTypeIndex
        if class_id == 114:
            r.p += 16
        r.p += 16
        types.append(class_id)
    objects = []
    for _ in range(r.i32()):
        r.align()
        table_pos = r.p
        path_id = r.i64()
        start = r.u32()
        size = r.u32()
        type_index = r.i32()
        objects.append({"path_id": path_id, "start": start, "size": size,
                        "class_id": types[type_index], "table_pos": table_pos})
    return {"meta_size": meta_size, "file_size": file_size, "data_offset": data_offset,
            "unity_version": unity_version, "objects": objects}


def build_settings_layout(data, info):
    obj = next(o for o in info["objects"] if o["class_id"] == BUILD_SETTINGS)
    base = info["data_offset"] + obj["start"]
    r = Reader(data, base)
    scenes = r.str_list()
    plugins = r.str_list()
    vr_start = r.p
    vr_devices = r.str_list()
    vr_end = r.p
    tags = r.str_list()
    guid = r.astr()
    return {"obj": obj, "base": base, "scenes": scenes, "plugins": plugins,
            "vr_devices": vr_devices, "vr_start": vr_start, "vr_end": vr_end,
            "tags": tags, "guid": guid}


def encode_str_list(items):
    out = bytearray(struct.pack("<i", len(items)))
    for s in items:
        b = s.encode("utf-8")
        out += struct.pack("<i", len(b)) + b
        out += b"\0" * ((-len(out)) % 4)
    return bytes(out)


def inspect(path):
    data = open(path, "rb").read()
    info = parse(data)
    bs = build_settings_layout(data, info)
    print(f"unity {info['unity_version']}, {len(info['objects'])} objects, data offset {info['data_offset']}")
    print(f"scenes ({len(bs['scenes'])}): {bs['scenes'][:3]} ...")
    print(f"preloadedPlugins: {bs['plugins']}")
    print(f"enabledVRDevices: {bs['vr_devices']}")
    print(f"buildTags: {bs['tags']}")
    print(f"buildGUID: {bs['guid']}")
    aligns = sorted({o['start'] % 16 for o in info['objects']})
    print(f"object start offsets mod 16: {aligns}")
    return data, info, bs


def patch(path, devices):
    data, info, bs = inspect(path)
    if bs["vr_devices"] == devices:
        print("already patched")
        return
    backup = path + BACKUP_SUFFIX
    if not os.path.exists(backup):
        shutil.copy2(path, backup)
        print(f"backup -> {backup}")

    obj = bs["obj"]
    old = data[bs["base"]:bs["base"] + obj["size"]]
    rel_start = bs["vr_start"] - bs["base"]
    rel_end = bs["vr_end"] - bs["base"]
    new_obj = old[:rel_start] + encode_str_list(devices) + old[rel_end:]

    # Rebuild the data region in the original order, keeping 8-byte object alignment.
    head = bytearray(data[:info["data_offset"]])
    body = bytearray()
    for o in sorted(info["objects"], key=lambda o: o["start"]):
        body += b"\0" * ((-len(body)) % 8)
        payload = new_obj if o is obj else data[info["data_offset"] + o["start"]:
                                                info["data_offset"] + o["start"] + o["size"]]
        struct.pack_into("<II", head, o["table_pos"] + 8, len(body), len(payload))
        body += payload
    out = head + body
    struct.pack_into(">I", out, 4, len(out))
    tmp = path + ".tmp"
    with open(tmp, "wb") as f:
        f.write(out)
    os.replace(tmp, path)
    print(f"patched enabledVRDevices -> {devices}")
    inspect(path)


def restore(path):
    backup = path + BACKUP_SUFFIX
    if not os.path.exists(backup):
        raise SystemExit(f"no backup at {backup}")
    shutil.copy2(backup, path)
    print(f"restored {path}")


if __name__ == "__main__":
    if len(sys.argv) < 3:
        raise SystemExit(__doc__)
    cmd, target = sys.argv[1], sys.argv[2]
    if cmd == "inspect":
        inspect(target)
    elif cmd == "patch":
        patch(target, sys.argv[3].split(",") if len(sys.argv) > 3 else ["None", "OpenVR"])
    elif cmd == "restore":
        restore(target)
    else:
        raise SystemExit(__doc__)
