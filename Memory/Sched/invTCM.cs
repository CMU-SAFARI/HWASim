using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedInvTCM : SchedTCM
    {
        public SchedInvTCM(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
        }

        // Override this for other algorithms
        override protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
        {
            if(winner == null)
                winner = candidate;
            MemoryRequest req1 = winner.mreq;
            MemoryRequest req2 = candidate.mreq;
            int rank1 = rank[req1.request.requesterID];
            int rank2 = rank[req2.request.requesterID];
            if (rank1 != rank2) {
                if (rank1 < rank2) return winner;
                else return candidate;
            }

            bool hit1 = winner.IsRowBufferHit;
            bool hit2 = candidate.IsRowBufferHit;
            if (hit1 ^ hit2) {
                if (hit1) return winner;
                else return candidate;
            }

            if (candidate.IsOlderThan(winner)) return candidate;
            else return winner;
        }

    }
}
