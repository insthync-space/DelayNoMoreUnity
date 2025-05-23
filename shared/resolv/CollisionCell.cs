using System;
using System.Collections.Generic;
using System.Text;

namespace shared {
    public class CollisionCell {
        public int X, Y;         // The X and Y position of the cell in the Space - note that this is in Grid position, not World position.
        public FrameRingBuffer<Collider> Colliders; // The colliders that a CollisionCell contains. Using "FrameRingBuffer" here for the ease of traversal

        public CollisionCell(int x, int y) {
            X = x;
            Y = y;
            Colliders = new FrameRingBuffer<Collider>(128); // A single cell is so small thus wouldn't have many touching colliders simultaneously
        }

        public bool HasSeen(Collider collider) {
            for (int i = Colliders.StFrameId; i < Colliders.EdFrameId; i++) {
                var (ok, o) = Colliders.GetByFrameId(i);
                if (ok && o == collider) {
                    return true;
                }
            }
            return false;
        }

        public void register(Collider collider) {
            if (HasSeen(collider)) return;
            if (Colliders.Cnt >= Colliders.N) {
                throw new ArgumentException(String.Format("Cell.Colliders is already full! X={0}, Y={1}, Cnt={2}, N={3}: trying to insert collider.Shape={4}, collider.Data={5}", X, Y, Colliders.Cnt, Colliders.N, collider.Shape, collider.Data));
            }
            Colliders.Put(collider);
        }

        public void unregister(Collider collider) {
            if (null == collider.Data) {
                // throw new ArgumentNullException("Are you trying to unregister a static barrier collider? " + collider.Shape.ToString(false));
            }
            for (int i = Colliders.StFrameId; i < Colliders.EdFrameId; i++) {
                var (ok, o) = Colliders.GetByFrameId(i);
                if (ok && o == collider) {
                    // swap with the st element
                    var (_, curStEle) = Colliders.GetByFrameId(Colliders.StFrameId);
                    if (null != curStEle) {
                        Colliders.SetByFrameId(curStEle, i);
                        // pop the current st element
                        Colliders.Pop();
                        break;
                    } else {
                        String msg = String.Format("Unexpected null st element for FrameRingBuffer `CollisionCell.Colliders` at i={0}, stFrameId={1}", i, Colliders.StFrameId);
                        throw new ArgumentException(msg);
                    }
                }
            }
        }

        /*
        [WARNING]

        What do "Cell.registerToTail & unregisterFromTail" achieve here?

        For static colliders,
        1. Their "Collider.TouchingCells" never change regardless of rollback
        2. Their orders in each Cell never change, i.e. dynamic colliders are always appended and removed within each Cell.Colliders after static ones, regardless of rollback

        For dynamic colliders,
        1. When each of them is removed, the corresponding "Cell.Colliders" will change their "Ed & EdFrameId" to guarantee no contamination for next "Step(...)" use, regardless of rollback
        2. After all dynamic colliders are removed at the end of "Step(...)", the whole CollisionSpace resumes regardless of the count or shapes of the dynamic colliders processed in current "Step(...)" -- most notably, each "Cell.Colliders,St & Ed & StFrameId & EdFrameId" wouldn't change! 

        The original "cell.unregister(...)" will break order of static and dynamic colliders in cell (by "swapping st element with to-delete-target and then pop"), which could impact rollback determinism! 
        */
        public void registerToTail(Collider collider) {
            if (Colliders.Cnt >= Colliders.N) {
                throw new ArgumentException(String.Format("Cell.Colliders is already full! X={0}, Y={1}, Cnt={2}, N={3}: trying to insert collider.Shape={4}, collider.Data={5}", X, Y, Colliders.Cnt, Colliders.N, collider.Shape, collider.Data));
            }
            Colliders.Put(collider);
        }

        public void unregisterFromTail(Collider collider) {
            Colliders.PopTail();
        }

        public void unregisterAll() {
            while (0 < Colliders.Cnt) {
                var (ok, collider) = Colliders.Pop();
                if (ok && null != collider) {
                    collider.clearTouchingCellsAndData();
                }
            }
        }

        public bool Occupied() {
            return (0 < Colliders.Cnt);
        }

        public String toStaticColliderShapeStr() {
            var staticColliderList = new List<String>();
            var sb = new StringBuilder();
            sb.Append(String.Format("Cell at x:{0}, y:{1}, static colliders :[", X, Y));
            for (int i = Colliders.StFrameId; i < Colliders.EdFrameId; i++) {
                var (ok, o) = Colliders.GetByFrameId(i);
                if (ok && null != o) {
                    if (null == o.Data) {
                        staticColliderList.Add("{ " + o.Shape.ToString(false) + "}");
                    }
                } else {
                    String msg = String.Format("Unexpected null for FrameRingBuffer `CollisionCell.Colliders` at i={0}", i);
                    throw new ArgumentException(msg);
                }
            }
            sb.Append(String.Format("{0}\n]", String.Join('\n', staticColliderList)));

            return sb.ToString();
        }
    }
}
