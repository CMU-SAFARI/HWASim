using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class Bank
    {
        public int index;

        protected DRAM mem;
        protected Channel chan;
        protected Rank rank; // rank to which this bank belongs
        public ulong nextActivation = 0;
        public ulong nextPrecharge = 0;
        public ulong nextRead = 0;
        public ulong nextWrite = 0;
	/*        protected ulong nextActivation = 0;
        public ulong nextPrecharge = 0;
        protected ulong nextRead = 0;
        protected ulong nextWrite = 0;
	*/
        protected ulong currentPage = DRAM.CLOSED;
        protected bool needsPrecharge = false;
        protected bool modified = false;
        protected int requesterID = 0;

        protected uint cRAS;
        protected uint cCAS;
        protected uint cWR;
        protected uint cDQS;
        protected uint cWTR;
        protected uint cRCD;
        protected uint cRP;
        protected uint cRTP;
        protected uint cRC;
        protected uint cRRD;

        protected ulong Max(ulong x,ulong y)
        {
            if(x > y) return x;
            else return y;
        }
        protected ulong Max(ulong x,ulong y,ulong z)
        {
            return Max(Max(x,y),z);
        }
        protected ulong Now { get { return Simulator.CurrentRound; } }

        public Bank(int index, DRAM mem, Channel chan, Rank rank)
        {
            this.index = index;
            this.mem = mem;
            this.chan = chan;
            this.rank = rank;

            cRAS = Config.memory.cRAS;
            cCAS = Config.memory.cCAS;
            cWR  = Config.memory.cWR;
            cDQS = Config.memory.cDQS;
            cWTR = Config.memory.cWTR;
            cRCD = Config.memory.cRCD;
            cRP  = Config.memory.cRP;
            cRTP = Config.memory.cRTP;
            cRC  = Config.memory.cRC;
            cRRD = Config.memory.cRRD;
        }

        public bool IsOpen(ulong page)
        {
            return currentPage == page;
        }

        //public bool IsClosed { get { return currentPage == DRAM.CLOSED; } }
        public bool IsClosed { get { return !needsPrecharge; } }


        public bool RequestCanIssue(SchedBuf buf)
        {
            ulong pageIndex = buf.mreq.shift_row;

//            uint burst = buf.burstLength;
            if(IsOpen(pageIndex))
            {
                if(buf.mreq.isWrite)
                    return (nextWrite <= Now) && mem.BusAvailable(cDQS,buf.burstLength);
                else
                    return (nextRead <= Now) && mem.BusAvailable(cCAS,buf.burstLength);
            }
            else if(IsClosed) // need to issue activation
                return (nextActivation <= Now) && (rank.nextActivation <= Now);
            else // conflict
                return nextPrecharge <= Now;
        }

        public void IssueCommand(SchedBuf buf)
        {
            MemoryRequest mreq = buf.mreq;
            ulong pageIndex = mreq.shift_row;
            if(Simulator.network.nodes[mreq.request.requesterID].m_cpu.m_stats_active)
                Simulator.stats.dramreqs_persrc[mreq.request.requesterID].Add();
            if(IsOpen(pageIndex)) // issue read/write
            {
                buf.whenCompleted = Now + cCAS + buf.burstLength;
                buf.whenIdle = Now + cCAS;
                if(buf.mreq.isWrite)
                {
//		    if( mem.chan.unIssueRequestsPerCore[17] > 0 ) 
//		    if( mreq.request.requesterID == 18 )
//		    if(( Simulator.CurrentRound > 5041500 ) && ( Simulator.CurrentRound < 5294000 ))
//			Console.WriteLine("LOG-WRITE, ch:{2}, id:{0}, bank:{1}", mreq.request.requesterID, mreq.bank_index, chan.mem_id );
                    nextWrite = Now + buf.burstLength;
                    nextRead = Max(nextRead,Now + cWTR);
                    nextPrecharge = Max(nextPrecharge,Now + cWR);
                    mem.UseBus(cDQS,buf.burstLength);
                    mem.chan.writeRequests++;
                    modified = true;
                    if(Simulator.network.nodes[mreq.request.requesterID].m_cpu.m_stats_active)
                        Simulator.stats.DRAMWriteRowBufferHitsPerSrc[mreq.request.requesterID].Add();
                }
                else
                {
//		    if( mem.chan.unIssueRequestsPerCore[17] > 0 ) 
//		    if( mreq.request.requesterID == 18 )
//			Console.WriteLine("LOG-READ, cycle:{4}, ch:{2}, id:{0}, bank:{1}, addr:{3:X}", mreq.request.requesterID, mreq.bank_index, chan.mem_id, mreq.request.address, Simulator.CurrentRound );
                    nextRead = Max(nextRead,Now + buf.burstLength);
                    nextPrecharge = Max(nextPrecharge,Now + cRTP);
                    mem.UseBus(cCAS,buf.burstLength);
                    mem.chan.readRequests++;
                    Simulator.stats.DRAMReadRowBufferHitsPerSrc[mreq.request.requesterID].Add();
                }
                buf.moreCommands = false; // no more commands to issue after this
            }
            else if(IsClosed) // issue activation
            {
//                ulong whenDone = Now;
//                ulong prechargeStart = Now;
//                ulong activateStart = Now;

//		if( mem.chan.unIssueRequestsPerCore[17] > 0 ) 
//		if( mreq.request.requesterID == 18 )
//		if(( Simulator.CurrentRound > 5041500 ) && ( Simulator.CurrentRound < 5294000 ))
//		    Console.WriteLine("LOG-ACT, ch:{4}, id:{0}, bank:{1}, page:{2:x}, arrived:{3}", mreq.request.requesterID, mreq.bank_index, pageIndex, buf.whenArrived, chan.mem_id );
                nextRead = Now + cRCD;
                nextWrite = Now + cRCD;
                nextPrecharge = Max(nextPrecharge,Now + cRAS);
                nextActivation = Max(nextActivation,Now + cRC);
                rank.nextActivation = Now + cRRD;
                currentPage = pageIndex;
                requesterID = mreq.request.requesterID;
                buf.whenIdle = Now + cRCD;
                if(Simulator.network.nodes[mreq.request.requesterID].m_cpu.m_stats_active)
                    Simulator.stats.DRAMActivationsPerSrc[buf.mreq.request.requesterID].Add();
                buf.issuedActivation = true;
                needsPrecharge = true;
            }
            else // conflict, issue precharge
            {
//		if( mem.chan.unIssueRequestsPerCore[17] > 0 ) 
//		    if( mreq.request.requesterID == 18 )
//		if(( Simulator.CurrentRound > 5041500 ) && ( Simulator.CurrentRound < 5294000 ))
//		    Console.WriteLine("LOG-PRE, ch:{4}, id:{0}, bank:{1}, page:{2:x}, Arrived:{3}, nextWrite:{5}, nextRead:{6}, nextActivation:{7}, nextPrecharge:{8}", mreq.request.requesterID, mreq.bank_index, pageIndex, buf.whenArrived, chan.mem_id, nextWrite, nextRead, nextActivation, nextPrecharge );
                nextActivation = Now + cRP;
                nextPrecharge = Now + cRP;
                buf.whenIdle = Now + cRP;
                needsPrecharge = false;
                Simulator.stats.DRAMPrechargesPerSrc[buf.mreq.request.requesterID].Add();
                Simulator.stats.DRAMConflictsPerSrc[buf.mreq.request.requesterID].Add();
            }
        }
    }

    public class Rank
    {
        public int numBanks;
        public int index;
        public Bank[] banks;
        protected DRAM mem;
        protected Channel chan;

        public ulong nextActivation = 0;

        public Rank(int index, DRAM mem, Channel chan)
        {
            this.index = index;
            this.mem = mem;
            this.chan = chan;
            numBanks = Config.memory.numBanks;
            banks = new Bank[numBanks];
            for(int i=0;i<numBanks;i++)
                banks[i] = new Bank(i,mem,chan,this);
        }

        public bool RequestCanIssue(SchedBuf buf)
        {
            int bank_index = buf.mreq.bank_index;
            return banks[bank_index].RequestCanIssue(buf);
        }

        public void IssueCommand(SchedBuf buf)
        {
            int bank_index = buf.mreq.bank_index;
            banks[bank_index].IssueCommand(buf);
        }
    }

    public class DRAM
    {
        public const ulong CLOSED = ulong.MaxValue;

        protected int numRanks;
        protected int numBanks;
        protected uint busRatio;
        protected uint busWidth;

        public Channel chan = null;

        public Rank[] ranks;
        protected ulong[] dataBus;

        public DRAM(Channel chan)
        {
            this.chan = chan;

            numRanks = Config.memory.numRanks;
            numBanks = Config.memory.numBanks;
            busWidth = Config.memory.busWidth;
            busRatio = Config.memory.busRatio;

            ranks = new Rank[numRanks];
            for(int i=0;i<numRanks;i++)
                ranks[i] = new Rank(i,this,chan);

            dataBus = new ulong[20]; // this covers 64*Length bus cycles into the future
            for(int i=0;i<dataBus.Length;i++)
                dataBus[i] = 0;
        }

        public bool RequestCanIssue(SchedBuf buf)
        {
            return ranks[buf.mreq.rank_index].RequestCanIssue(buf);
        }

        public void IssueCommand(SchedBuf buf)
        {
            ranks[buf.mreq.rank_index].IssueCommand(buf);
        }

        // when is relative from the current cycle
        public bool BusAvailable(uint when, uint burst)
        {
            uint normWhen = when / busRatio;
            for(uint i=0;i<burst;i++)
            {
                uint absOffset = normWhen + i;
                uint idx = absOffset / 64;
                uint offset = absOffset & 63;
                if(idx >= dataBus.Length)
                {
                    Console.WriteLine("dataBus index of {0} requested when max size is {1}... extending",idx,dataBus.Length);
                    ulong[] newBus = new ulong[idx+4];
                    for(int j=0;j<dataBus.Length;j++)
                        newBus[j] = dataBus[j];
                    for(int j=dataBus.Length;j<newBus.Length;j++)
                        newBus[j] = 0;
                    dataBus = newBus;
                }
                if(((dataBus[idx] >> (int)offset) & 1) == 1)
                    return false;
            }
            return true;
        }
        
        // when is relative from the current cycle
        public void UseBus(uint when, uint burst)
        {
            uint normWhen = when / busRatio;
            for(uint i=0;i<burst;i++)
            {
                uint absOffset = normWhen + i;
                uint idx = absOffset / 64;
                uint offset = absOffset & 63;
                Debug.Assert(idx < dataBus.Length);
                dataBus[idx] |= (((ulong)1) << (int)offset);
            }
        }

        protected void BusTick()
        {
            for(int i=0;i<dataBus.Length;i++)
            {
                dataBus[i] >>= 1;
                if(i < (dataBus.Length-1))
                    dataBus[i] |= ((dataBus[i+1]&1)) << 63;
            }
        }

        public void Tick()
        {
            if(!BusAvailable(0,1))
                Simulator.stats.DRAMBusUtilization[chan.mem_id*Config.memory.numChannels+chan.id].Add();
            BusTick();
        }
    }
}
