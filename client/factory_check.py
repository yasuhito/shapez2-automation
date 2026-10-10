"""Repeat an observed native-game command sequence; the game owns simulation and delivery."""
import argparse, json, time
from pathlib import Path
from bridge_client import Bridge

SEED = 2162800
PLAN = ([('miner',25,5,2),('left',24,5,2)]
        + [('belt',24,y,1) for y in range(6,9)]
        + [('right',24,9,1),('belt',23,9,2),('sender',22,9,2),('receiver',17,9,2)]
        + [('belt',x,9,2) for x in range(16,-1,-1)] + [('sender',-1,9,2)])

class RecordedBridge:
    def __init__(self, bridge, output):
        self.bridge, self.output, self.records = bridge, Path(output), []
        self.output.parent.mkdir(parents=True, exist_ok=True)
    def call(self, op, **args):
        row = {'op': op, 'args': args}
        try:
            result = self.bridge.call(op, **args)
            row['result'] = result
            return result
        except Exception as error:
            row['error'] = str(error)
            raise
        finally:
            self.records.append(row)
            self.output.write_text(json.dumps(self.records, indent=2))

def wait_for_menu(b):
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        s = b.call('global_state')
        if s['transition_error']: raise RuntimeError(s['transition_error'])
        if s['game_state'] == 'InMainMenu' and not s['transition_pending']: return
        time.sleep(.25)
    raise TimeoutError('menu_not_ready; do not retry transition automatically')

def cleanup_owned(b):
    s = b.call('state'); save = s['save_id']
    expected = {(x,y,0) for kind,x,y,rotation in PLAN}
    actual = {(k['x'],k['y'],k['z']) for k in s['buildings']}
    if actual != expected: raise RuntimeError('unexpected_layout; refusing automatic cleanup')
    b.call('pause', save_id=save, paused=True)
    for kind,x,y,rotation in reversed(PLAN): b.call('delete_owned',save_id=save,x=x,y=y,z=0)
    b.call('delete_owned_island',save_id=save,x=1,y=0,z=0)
    restored = b.call('state')
    if restored['buildings'] or len(restored['islands']) != 1: raise RuntimeError('cleanup_not_restored')
    b.call('main_menu',save_id=save)
    wait_for_menu(b)
    return restored

def run_once(b):
    wait_for_menu(b)
    b.call('select_mode',mode='RegularGameMode')
    b.call('start_new',seed=SEED,test_only=True)
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        status=b.call('global_state')
        if status['transition_error']: raise RuntimeError(status['transition_error'])
        if status['game_state']=='InGame' and not status['transition_pending']: break
        time.sleep(.25)
    else: raise TimeoutError('new_game_not_ready; do not retry automatically')
    initial=b.call('state'); save=initial['save_id']
    if b.call('global_state').get('assignment_confirmation'):
        b.call('accept_assignment',save_id=save,accept=True)
        confirmation_deadline=time.monotonic()+10
        while b.call('global_state').get('assignment_confirmation'):
            if time.monotonic()>confirmation_deadline:raise TimeoutError('assignment_confirmation_did_not_close')
            time.sleep(.1)
    if initial['buildings']: raise RuntimeError('initial_layout_not_empty')
    b.call('pause',save_id=save,paused=True)
    b.call('place_island',save_id=save,test_only=True,definition='Layout_ShapeMiner',x=1,y=0,z=0,rotation=2)
    hashes=[]
    for kind,x,y,rotation in PLAN:
        s=b.call('place',save_id=save,test_only=True,kind=kind,x=x,y=y,z=0,rotation=rotation)
        hashes.append(s['layout_hash'])
    before=b.call('state')
    if before['stored_shapes']: raise RuntimeError('unexpected_initial_deliveries')
    b.call('pause',save_id=save,paused=False)
    deadline=time.monotonic()+55
    while time.monotonic()<deadline:
        after=b.call('state')
        if sum(k['amount'] for k in after['stored_shapes'])>=5: break
        time.sleep(1)
    else: raise TimeoutError('native_delivery_not_observed_within_55_seconds')
    if after['layout_hash']!=before['layout_hash']: raise RuntimeError('layout_changed_while_running')
    return {'seed':SEED,'save_id':save,'initial_buildings_hash':initial['layout_hash'],
            'placement_hashes':hashes,'factory_buildings_hash':before['layout_hash'],
            'delivered':after['stored_shapes'],'focused':after['context']['focused'],
            'scope':'Same seeded map and command sequence; structure and actual delivery verified. Complete simulator determinism is not established.'}

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--ipc',required=True);p.add_argument('--out',required=True);p.add_argument('--cleanup-current',action='store_true');a=p.parse_args()
    b=RecordedBridge(Bridge(a.ipc),Path(a.out)/'commands.json')
    if a.cleanup_current: cleanup_owned(b)
    result=run_once(b);Path(a.out,'result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))
