"""Copy chosen prefabs out of a Synty Godot pack zip, with everything they reference.

    python tools/synty_copy.py data/inbox/POLYGON_Gang_Warfare_Godot_4_6_2_v1_0_1.zip SM_Prop_Bottle_01 SM_Prop_Range_Target_01

Only what is used goes into the project (CLAUDE.md). Synty assets are licensed and never committed: they land under
Assets/Synty/<Pack>/ (git-ignored), with the pack's licence file. References are followed through .tscn, .tres and
.material files (res:// paths, matched case-insensitively, since the packs mix "assets/" and "Assets/").
"""
import os, re, sys, zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    z = zipfile.ZipFile(sys.argv[1])
    names = z.namelist()
    # res:// path (lower case, from "assets/") -> zip entry
    by_res = {}
    for n in names:
        i = n.lower().find("assets/")
        if i >= 0 and not n.endswith("/"):
            by_res[n[i:].lower()] = n
    wanted = []
    for prefab in sys.argv[2:]:
        hits = [n for n in names if n.split("/")[-1] == prefab + ".tscn"]
        if not hits:
            sys.exit(f"no prefab {prefab}.tscn in {sys.argv[1]}")
        wanted.append(hits[0])
    done, todo = set(), list(wanted)
    while todo:
        entry = todo.pop()
        if entry in done:
            continue
        done.add(entry)
        if entry.endswith((".tscn", ".tres", ".material")):
            text = z.read(entry).decode("utf-8", "replace")
            for ref in re.findall(r'path="res://([^"]+)"', text):
                hit = by_res.get(ref.lower())
                if hit:
                    todo.append(hit)
                else:
                    print(f"  missing in zip: {ref}")
    copied, packs = 0, set()
    for entry in sorted(done):
        i = entry.lower().find("assets/")
        rel = "Assets/" + entry[i + len("assets/"):]   # one spelling on disk: "Assets/"
        parts = rel.split("/")
        packs.add("/".join(parts[:3]) if parts[1] == "Synty" else "/".join(parts[:2]))
        dest = os.path.join(ROOT, *parts)
        os.makedirs(os.path.dirname(dest), exist_ok=True)
        with open(dest, "wb") as f:
            f.write(z.read(entry))
        copied += 1
    # the Godot packs carry no licence file: leave a note beside the files, as for the other packs
    for pack in packs:
        note = os.path.join(ROOT, *pack.split("/"), "LICENSE.txt")
        if not os.path.exists(note):
            with open(note, "w", encoding="utf-8") as f:
                f.write(f"Synty Studios {os.path.basename(pack)} (assets used by Baltic Drive). Licensed to Tom; never commit.\n")
    print(f"{copied} files copied for {len(wanted)} prefabs into {', '.join(sorted(packs))}")


if __name__ == "__main__":
    main()
