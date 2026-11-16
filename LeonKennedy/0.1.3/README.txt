Leon camera compatibility update by Ikta

The Python script applies the prefab name changes to the original Leon release ZIP. Use Python
with UnityPy 1.25.4 installed and run:

python patch-leon-bundles.py ORIGINAL_LEON.zip PATCHED_LEON.zip

The script renames duplicate camera-anchor objects and verifies that all other
serialized objects are byte-identical within the four model bundles.
