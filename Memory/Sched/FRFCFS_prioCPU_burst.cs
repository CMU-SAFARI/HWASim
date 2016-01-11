using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedFRFCFS_PrioCPUWhenNonBursty : Scheduler
    {
        public Channel ch;
        public SchedFRFCFS_PrioCPUWhenNonBursty(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
            ch = chan;
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
            else if(ch.triggerCPUPrio && winner.mreq.from_GPU && !candidate.mreq.from_GPU) //Priritize CPU if GPU is not bursty
                winner = candidate;
            else if(!winner.IsRowBufferHit && candidate.IsRowBufferHit) // prev not RB hit
                winner = candidate;
            else if(candidate.IsOlderThan(winner))
                winner = candidate;
            return winner;
        }
    }
}
