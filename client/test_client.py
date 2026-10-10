import unittest
from bridge_client import vertical
class RecordingBackend:
 def __init__(self,occupied=False,drift=False):self.present=occupied;self.drift=drift;self.paused=False;self.calls=[]
 def state(self):return {'save_id':'test-session','paused':self.paused,'layout_hash':'placed' if self.present else ('drift' if self.drift and any(op=='delete_owned' for op,_ in self.calls) else 'empty'),'buildings':[{'x':1,'y':2,'z':0}] if self.present else []}
 def call(self,op,**args):
  self.calls.append((op,args))
  if op=='pause':self.paused=args['paused']
  if op=='place_belt':self.present=True
  if op=='delete_owned':self.present=False
  return self.state()
class Tests(unittest.TestCase):
 def test_repeated_rounds_restore_layout(self):
  self.assertEqual([r['after_hash'] for r in vertical(RecordingBackend(),1,2,0,0,2)['rounds']],['empty','empty'])
 def test_occupied_cell_is_not_replaced(self):
  with self.assertRaisesRegex(RuntimeError,'occupied'):vertical(RecordingBackend(occupied=True),1,2,0,0,1)
 def test_unexpected_layout_drift_is_rejected(self):
  with self.assertRaisesRegex(RuntimeError,'layout_not_restored'):vertical(RecordingBackend(drift=True),1,2,0,0,1)
 def test_pause_state_restored_after_error(self):
  b=RecordingBackend(occupied=True)
  try:vertical(b,1,2,0,0,1)
  except RuntimeError:pass
  self.assertFalse(b.paused)
if __name__=='__main__':unittest.main()
