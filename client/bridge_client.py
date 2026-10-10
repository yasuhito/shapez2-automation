"""Local file protocol. No server, ports, user input synthesis, or authentication changes."""
import argparse,json,os,time
from pathlib import Path
class Bridge:
 def __init__(self,root):
  self.root=Path(root);self.requests=self.root/'requests';self.responses=self.root/'responses'
  if not self.requests.is_dir() or not self.responses.is_dir():raise RuntimeError('bridge_not_running: IPC directories absent')
 def call(self,op,timeout=10,**args):
  ids=[int(p.stem) for p in self.requests.glob('*.json') if p.stem.isdigit()]
  request_id=max(ids,default=0)+1
  name=f'{request_id:020d}.json';request={'id':request_id,'op':op,**args}
  payload=json.dumps(request,separators=(',',':')).encode();temp=self.requests/(name+'.tmp')
  with temp.open('xb') as f:f.write(payload);f.flush();os.fsync(f.fileno())
  os.link(temp,self.requests/name);temp.unlink()
  deadline=time.monotonic()+timeout;path=self.responses/name
  while not path.exists():
   if time.monotonic()>deadline:raise TimeoutError('response_timeout; outcome unknown; do not retry mutation automatically')
   time.sleep(.025)
  result=json.loads(path.read_text())
  if not result.get('ok'):raise RuntimeError(result.get('error','unknown_error'))
  if result.get('id')!=request_id:raise RuntimeError('response_id_mismatch')
  return result['result']
def vertical(bridge,x,y,z,rotation,rounds):
 initial=bridge.call('state');save=initial['save_id'];paused=initial['paused'];records=[]
 bridge.call('pause',save_id=save,paused=True)
 try:
  for i in range(rounds):
   before=bridge.call('state')
   if before['save_id']!=save:raise RuntimeError('save_changed')
   if any((b['x'],b['y'],b['z'])==(x,y,z) for b in before['buildings']):raise RuntimeError('test_position_occupied')
   created=False
   try:
    after=bridge.call('place_belt',save_id=save,test_only=True,x=x,y=y,z=z,rotation=rotation);created=True
    if not any((b['x'],b['y'],b['z'])==(x,y,z) for b in after['buildings']):raise RuntimeError('placement_not_observed')
    observed=bridge.call('state')
    if observed['layout_hash']!=after['layout_hash']:raise RuntimeError('read_after_write_changed')
   finally:
    if created:bridge.call('delete_owned',save_id=save,x=x,y=y,z=z)
   cleared=bridge.call('state')
   if cleared['layout_hash']!=before['layout_hash']:raise RuntimeError('layout_not_restored')
   records.append({'round':i+1,'before_hash':before['layout_hash'],'placed_hash':after['layout_hash'],'after_hash':cleared['layout_hash']})
 finally:
  bridge.call('cancel',save_id=save)
  bridge.call('pause',save_id=save,paused=paused)
 return {'live_game_verified':True,'save_id':save,'rounds':records,'determinism_scope':'layout mutation/read-back only, not all simulation state'}
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--ipc',required=True);p.add_argument('op',choices=['state','vertical','ping','global_state','modes','call']);p.add_argument('--x',type=int);p.add_argument('--y',type=int);p.add_argument('--z',type=int,default=0);p.add_argument('--rotation',type=int,default=0);p.add_argument('--rounds',type=int,default=2);p.add_argument('--command-json');a=p.parse_args();b=Bridge(a.ipc)
 if a.op=='vertical' and (a.x is None or a.y is None):p.error('--x and --y required')
 if a.op=='call':
  if not a.command_json:p.error('--command-json required')
  q=json.loads(a.command_json);op=q.pop('op');result=b.call(op,**q)
 elif a.op=='vertical':result=vertical(b,a.x,a.y,a.z,a.rotation,a.rounds)
 else:result=b.call(a.op)
 print(json.dumps(result,indent=2))
