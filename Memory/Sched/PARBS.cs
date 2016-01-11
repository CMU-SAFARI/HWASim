using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedPARBS : Scheduler
    {
        protected int markedRequestsRemaining = 0;
        protected int[,,] bankLoad;
        protected int[] maxBankLoad;
        protected int[] totalLoad;
        protected int[] overallRank;

        public SchedPARBS(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
            bankLoad = new int[Config.Ng,Config.memory.numRanks,Config.memory.numBanks];
            maxBankLoad = new int[Config.Ng];
            totalLoad = new int[Config.Ng];
            overallRank = new int[Config.Ng];
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
            else if(!winner.marked && candidate.marked)
                winner = candidate;
            else if(!winner.IsRowBufferHit && candidate.IsRowBufferHit) // prev not RB hit
                winner = candidate;
            else if(winner.rank > candidate.rank)
                winner = candidate;
            else if(candidate.IsOlderThan(winner))
                winner = candidate;
            return winner;
        }

        override public void Tick()
        {
            base.Tick();

            if(markedRequestsRemaining <= 0)
            {
                for(int i=0;i<Config.Ng;i++)
                {
                    totalLoad[i] = 0;
                    maxBankLoad[i] = 0;
                    overallRank[i] = 0;
                    for(int r=0;r<Config.memory.numRanks;r++)
                        for(int b=0;b<Config.memory.numBanks;b++)
                            bankLoad[i,r,b] = 0;
                }

                // collect per-core loading information
                for(int i=0;i<buf.Length;i++)
                {
                    if(buf[i].Valid && buf[i].moreCommands)
                    {
                        int rID = buf[i].mreq.request.requesterID;
                        totalLoad[rID]++;
                        bankLoad[rID,buf[i].mreq.rank_index,buf[i].mreq.bank_index]++;
                        if(bankLoad[rID,buf[i].mreq.rank_index,buf[i].mreq.bank_index] > maxBankLoad[rID])
                            maxBankLoad[rID] = bankLoad[rID,buf[i].mreq.rank_index,buf[i].mreq.bank_index];
                    }
                }

                // sort load information
                List<int> loadList = new List<int>();
                for(int i=0;i<Config.Ng;i++)
                    loadList.Add(i);
                loadList.Sort(delegate(int x, int y)
                        {
                            if(maxBankLoad[x] != maxBankLoad[y])
                                return maxBankLoad[x].CompareTo(maxBankLoad[y]);
                            else
                                return totalLoad[x].CompareTo(totalLoad[y]);
                        });
                for(int i=0;i<Config.Ng;i++)
                {
                    overallRank[loadList[i]] = i;
                }

                // assign ranks
                for(int i=0;i<buf.Length;i++)
                {
                    if(buf[i].Valid && buf[i].moreCommands)
                    {
                        buf[i].rank = overallRank[buf[i].mreq.request.requesterID];
                    }
                }

                // perform batching
                // yuck: find oldest CAP requests per thread
                for(int t=0;t<Config.Ng;t++)
                {
                    List<int> requests = new List<int>();
                    for(int i=0;i<buf.Length;i++)
                        if(buf[i].Valid && buf[i].moreCommands)
                            requests.Add(i);
                    requests.Sort(delegate(int x,int y) { return buf[x].whenArrived.CompareTo(buf[y].whenArrived);});
                    for(int i=0;i<Config.memory.PARBSBatchCap && i<requests.Count;i++)
                    {
                        buf[requests[i]].marked = true;
                        markedRequestsRemaining++;
                    }
                }
            }
        }

        override public void MarkCompleted(SchedBuf buf)
        {
            buf.marked = false;
            markedRequestsRemaining--;
            Debug.Assert(markedRequestsRemaining >= 0);
        }
    }
}
