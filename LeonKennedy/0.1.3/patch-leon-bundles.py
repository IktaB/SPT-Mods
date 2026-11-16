import io,re,sys,zipfile
from pathlib import Path
import UnityPy
source,target=map(Path,sys.argv[1:3])
with zipfile.ZipFile(source) as old,zipfile.ZipFile(target,'w',compression=zipfile.ZIP_DEFLATED) as new:
 for entry in old.infolist():
  payload=old.read(entry)
  if re.search(r'/leon_(head|top|bottom|hands)\.bundle$',entry.filename):
   env=UnityPy.load(payload)
   objects={o.path_id:o for o in env.objects}
   transforms={o.path_id:o.read_typetree() for o in objects.values() if o.type.name=='Transform'}
   def game(t):return objects[t['m_GameObject']['m_PathID']]
   def in_copy(t):
    parent=t['m_Father']['m_PathID']
    while parent in transforms:
     if game(transforms[parent]).read().m_Name=='Tarkov Skeleton':return True
     parent=transforms[parent]['m_Father']['m_PathID']
    return False
   expected={i:o.get_raw_data() for i,o in objects.items()}
   changed=set()
   for t in transforms.values():
    obj=game(t)
    data=obj.read()
    if re.search(r'ribcage|chest|torso|spine[123]|upperbody|body',data.m_Name,re.I) and in_copy(t):
     data.m_Name=f'LeonBind_{obj.path_id}'
     data.save()
     changed.add(obj.path_id)
   assert len(changed)==4,(entry.filename,changed)
   payload=env.file.save(packer='lz4')
   actual={o.path_id:o.get_raw_data() for o in UnityPy.load(payload).objects}
   assert actual.keys()==expected.keys()
   assert {i for i in actual if actual[i]!=expected[i]}==changed
   print(entry.filename, len(changed),'names changed; all remaining objects byte-identical')
  new.writestr(entry.filename,payload)
with zipfile.ZipFile(target) as z:assert z.testzip() is None
