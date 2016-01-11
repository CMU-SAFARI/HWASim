using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedFRFCFS : Scheduler
    {
        public SchedFRFCFS(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
        }

        // Override this for other algorithms
        override protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
        {
            if(winner == null)
                winner = candidate;
            else if(!winner.Urgent && candidate.Urgent)
                winner = candidate;
            else if(winner.Urgent && candidate.Urgent && candidate.IsOlderThan(winner))
                winner = candidate;
            else if(!winner.IsRowBufferHit && candidate.IsRowBufferHit) // prev not RB hit
                winner = candidate;
            else if(candidate.IsOlderThan(winner))
                winner = candidate;
            return winner;
        }
    }
}
