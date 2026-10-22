import json
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "DiskArtifacts"


def image(path, cycle=False):
    data = bytearray(512 * 80)
    struct.pack_into("<H", data, 11, 512)
    data[13] = 1
    struct.pack_into("<H", data, 14, 32)
    data[16] = 1
    struct.pack_into("<I", data, 32, 80)
    struct.pack_into("<I", data, 36, 1)
    struct.pack_into("<I", data, 44, 2)
    data[510:512] = b"\x55\xaa"
    fat = 32*512
    struct.pack_into("<I", data, fat+8, 2 if cycle else 0x0fffffff)
    struct.pack_into("<I", data, fat+12, 0x0fffffff)
    root = 33*512
    data[root:root+11] = b"REPORT  TXT"
    data[root+11] = 0x20
    struct.pack_into("<H", data, root+26, 3)
    struct.pack_into("<I", data, root+28, 42)
    data[root+32:root+43] = b"\xe5RAFT   TXT"
    data[root+43] = 0x20
    data[root+64] = 0
    path.write_bytes(data)


class DiskTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        subprocess.run(["dotnet","restore",str(PROJECT),"--configfile",str(ROOT/"NuGet.Config")],check=True,capture_output=True,text=True)
        subprocess.run(["dotnet","build",str(PROJECT),"-c","Release","--no-restore"],check=True,capture_output=True,text=True)
        cls.dll = PROJECT / "bin" / "Release" / "net8.0" / "DiskArtifacts.dll"

    def test_known_image_and_bad_geometry(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "image.img"
            image(path)
            result = subprocess.run(["dotnet",str(self.dll),str(path)],capture_output=True,text=True)
            self.assertEqual(result.returncode,0,result.stderr)
            entries = json.loads(result.stdout)
            self.assertEqual([(e["Path"],e["Deleted"]) for e in entries],[("REPORT.TXT",False),("?RAFT.TXT",True)])
            self.assertEqual(entries[0]["Size"],42)
            corrupt=bytearray(path.read_bytes())
            corrupt[510:512]=b"\x00\x00"
            path.write_bytes(corrupt)
            self.assertNotEqual(subprocess.run(["dotnet",str(self.dll),str(path)],capture_output=True).returncode,0)


if __name__ == "__main__": unittest.main()

