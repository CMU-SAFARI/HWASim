using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedInvMPKI : Scheduler
    {
        public double[] mpki;
        public SchedInvMPKI(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
            mpki = new double[Config.Ng];
        }

        public void getMPKI()
        {
            for(int i=0;i<Config.Ng;i++)
            {
                mpki[i] = ((double)Simulator.stats.L1_misses_persrc[i].Count)/Simulator.stats.insns_persrc[i].Count;
            }
        } 

        // Override this for other algorithms
        override protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
        {
            getMPKI();
            if(winner == null)
                winner = candidate;
            else if(mpki[winner.mreq.request.requesterID] > mpki[candidate.mreq.request.requesterID])
                winner = candidate;
            else if(mpki[winner.mreq.request.requesterID] == mpki[candidate.mreq.request.requesterID])
            {
                if(!winner.Urgent && candidate.Urgent)
                    winner = candidate;
                else if(winner.Urgent && candidate.Urgent && candidate.IsOlderThan(winner))
                    winner = candidate;
                else if(!winner.IsRowBufferHit && candidate.IsRowBufferHit) // prev not RB hit
                    winner = candidate;
                else if(candidate.IsOlderThan(winner))
                    winner = candidate;
            }
            return winner;
        }
    }
}
