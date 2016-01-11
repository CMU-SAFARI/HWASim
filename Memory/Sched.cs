//#define PACKETDUMP
using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{
    public class SchedBuf
    {
        public MemoryRequest mreq=null;
        public ulong whenArrived=ulong.MaxValue;
        public ulong whenStarted=ulong.MaxValue;
        public ulong whenCompleted=ulong.MaxValue;
        public ulong whenIdle=0; // for sub-commands
        public bool moreCommands = true;
        public int index = -1;
        public uint burstLength = 4;
        public bool issuedActivation=false;
        public bool marked=false;
        public int rank=-1;

        protected DRAM mem;

	/* HWA CODE */
	public int wait_num=0;
	/* HWA CODE END */

        public SchedBuf(int index, DRAM mem)
        {
            this.index = index;
            this.mem = mem;
        }

        public void Allocate(MemoryRequest mreq)
        {
            this.mreq = mreq;
            mreq.buf_index = index;
            whenArrived = Simulator.CurrentRound;
            moreCommands = true;
            burstLength = mreq.mem_size / Config.memory.busWidth / 2;
        }

	/* HWA CODE */
	public void print_stat(int id)
	{
	    if( Simulator.network.nodes[mreq.request.requesterID].cpu.is_HWA() )
		Console.WriteLine("{0}-{4}:, {1},{2},{3}", mreq.request.requesterID, whenStarted-whenArrived, whenCompleted-whenStarted,wait_num,id);
	}

        public void Deallocate()
        {
            mreq.buf_index = -1;
            mreq=null;
            whenArrived=ulong.MaxValue;
            whenStarted=ulong.MaxValue;
            whenCompleted=ulong.MaxValue;
            whenIdle = 0;
            issuedActivation = false;
            marked = false;
            rank = -1;
	    /* HWA CODE */
	    wait_num=0;
	    /* HWA CODE END */
        }

        protected ulong Now { get { return Simulator.CurrentRound; } }

        public bool IsOlderThan(SchedBuf other)
        {
            return whenArrived < other.whenArrived;
        }
        public bool IsRowBufferHit { get { return mem.ranks[mreq.rank_index].banks[mreq.bank_index].IsOpen(mreq.shift_row); } }
        public bool FromGPU { get { return mreq.from_GPU;} }
        public bool IsWrite { get { return mreq.request.write; } }

        public bool Available { get { return mreq == null; } }
        public bool Valid { get { return mreq != null; } }
        public bool Started { get { return whenStarted <= Now; } }
        public bool Completed { get { return whenCompleted <= Now; } }
        public bool Busy { get { return whenIdle > Now; } }
        public bool Urgent { get { return mreq.from_GPU?((Now-whenArrived) >= (ulong)Config.memory.GPUUrgentThreshold):
                                                        ((Now-whenArrived) >= (ulong)Config.memory.coreUrgentThreshold); } }
        public bool SuperUrgent { get { return (Now-whenArrived) >= (ulong)Config.memory.SuperUrgentThreshold; } }
    }

    abstract public class Scheduler
    {
        public SchedBuf[] buf = null;
        protected DRAM mem = null;
        protected Channel chan = null;

        protected ulong lastIssue = 0;
        protected ulong Now { get { return Simulator.CurrentRound; } }

	/* HWA CODE */
	public SchedBuf winner;
	public bool[] schedMask;
	public int[] bank_reserve;
	public int[] bank_reserve_priority;
	public int   data_bus_reserved_priority;
	public bool[] bank_reserved_rowhit;

	/* HWA CODE END */

        // TODO:
        // To decide whether to cache a row or not...
        // Policy 1: Don't do it unless you're sure: scan SchedBuf and if there's more than one request for
        //           the row, then go ahead and cache it.
        // Policy 2: Each core gets a row.  If the row is not in use, is clean (no WB needed), or is too old,
        //           then cache it.  For additional (non-per-core), if no pending requests for that row, row
        //           is clean or too old, then cache it.
        // Policy 2b: Same as 2, but potentially steal rows from other cores if they are clean and old, or
        //            dirty and very, very old.
        // Policy 2c: variant of 2b: add a relative utility counter to each row.  Increment on each hit, and
        //            also track the number of requests overall by that core.  This judges how useful caching
        //            that row is relative to the request rate of that row.  Age (>>=1) when the overall
        //            request rate saturates its counter. If below threshold, allow other cores to steal.
        //            If above threshold, give preference for allocating non-core entries.
        // Coarse-control: If core consistently does not make good use of its cache entries, stop caching
        // any of its lines.

        // TODO: scheduling policies...
        // Policy 1: Simple RBRF hit first, RB hit first, FCFS
        // Policy 1b: Read first, RBRF hit first, RB hit first, FCFS
        // Policy 2: Keep tracking which requests would like to cache their rows, and schedule RBRF hits
        //           to same entry first to allow the entry to be reused sooner.

        public Scheduler()
        {
        }

        public Scheduler(SchedBuf[] buf, DRAM mem, Channel chan)
        {
            this.buf = buf;
            this.mem = mem;
            this.chan = chan;

	    schedMask = new bool[Config.Ng];
	    bank_reserve = new int[Config.memory.numBanks];
	    bank_reserve_priority = new int[Config.memory.numBanks];
	    bank_reserved_rowhit = new bool[Config.memory.numBanks];
	    data_bus_reserved_priority = 0;
	    
        }

        // Override this for other algorithms
        virtual protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
        {
            if(winner == null)
                winner = candidate;
            else if(!winner.IsRowBufferHit && candidate.IsRowBufferHit) // prev not RB hit
                winner = candidate;
            else if(candidate.IsOlderThan(winner))
                winner = candidate;
            return winner;
        }

	/* HWA CODE */
	// Override this for other algorithms
	virtual protected bool schedMaskCheck(SchedBuf tgt)
	{
	    if( Config.sched.hwa_str_priority )
	    {
		if( Config.sched.hwa_priority_per_bank )
		{
		    int bank_id = tgt.mreq.bank_index;
		    if( bank_reserve[bank_id] == int.MaxValue ) return true;
		    else if( getPriority(tgt) >= bank_reserve_priority[bank_id] ) return true;
		    else return false;
		}
		else
		{
		    return(schedMask[tgt.mreq.request.requesterID]);
		}
	    }
	    else
	    {
		return true;
	    }
	}
	virtual protected void schedMaskPrepare()
	{
	    if( Config.sched.hwa_str_priority )
	    {
		calculate_priority();

		if( Config.sched.hwa_priority_per_bank )
		{
		    for( int i = 0; i < Config.memory.numBanks; i++ )
		    {
			bank_reserve[i] = int.MaxValue;
			bank_reserved_rowhit[i] = false;
			bank_reserve_priority[i] = -1;
		    }
		    data_bus_reserved_priority = 0;

		    for( int i = 0; i < buf.Length; i++ )
		    {
			if(buf[i].Valid && buf[i].moreCommands && !buf[i].Busy)
			{
			    int req_id = buf[i].mreq.request.requesterID;
			    int bank_id = buf[i].mreq.bank_index;
			    int req_priority = getPriority(buf[i]);
			    if(( bank_reserve[bank_id] == int.MaxValue ) ||
			       ( bank_reserve_priority[bank_id] < req_priority ))
			    {	
				bank_reserve[bank_id] = req_id;
				bank_reserved_rowhit[bank_id] = buf[i].IsRowBufferHit;
				bank_reserve_priority[bank_id] = req_priority;
			    }
			    else if(( bank_reserve_priority[bank_id] == req_priority ) && buf[i].IsRowBufferHit )
				bank_reserved_rowhit[bank_id] = buf[i].IsRowBufferHit;
			    
//			    Console.WriteLine("SchedBuf{0}/{6}, pid:{1}, bank_id:{4}, pr:{2}, rowhit:{3}, marked:{5}", i, req_id, req_priority, buf[i].IsRowBufferHit, bank_id, buf[i].marked, chan.mem_id);
			}
		    }
		    for( int bid = 0; bid < Config.memory.numBanks; bid++ )
		    {
//			if(( Simulator.CurrentRound > 5041500 ) && ( Simulator.CurrentRound < 5294000 ))
//			    Console.WriteLine("Bank Reserve{0}:{1},{2}", bid, bank_reserve[bid],bank_reserve_priority[bid]);

			if( bank_reserved_rowhit[bid] )
			    if( data_bus_reserved_priority < bank_reserve_priority[bid] ) data_bus_reserved_priority = bank_reserve_priority[bid];
		    }
//		    if(( Simulator.CurrentRound > 5041500 ) && ( Simulator.CurrentRound < 5294000 ))
//			Console.WriteLine("DataBus Reserve priority:{0}", data_bus_reserved_priority);
		}
		else
		{
		    int max_priority = -1;
		    for( int pid = 0; pid < Config.Ng; pid++ )
		    {
			if(( chan.unIssueRequestsPerCore[pid] > 0 ) && ( getPriority(pid) > max_priority ))
			    max_priority = getPriority(pid);
		    }
		    for( int pid = 0; pid < Config.Ng; pid++ )
		    {
			if( getPriority(pid) >= max_priority )
			    schedMask[pid] = true;
			else 
			    schedMask[pid] = false;
		    }
		}
	    }
	    else
	    {
		for( int pid = 0; pid < Config.Ng; pid++ )
		{
		    schedMask[pid] = true;
		}
		 
	    }

	    return;
	}
	virtual protected bool schedResultMaskChk(SchedBuf tgt)
	{
	    if( Config.sched.hwa_str_priority && Config.sched.hwa_priority_per_bank )
	    {	
		if( tgt.IsRowBufferHit )
		{
		    int priority = getPriority(tgt);
		    if( priority >= data_bus_reserved_priority ) return true;
		    else 
		    {
//			Console.WriteLine("Winner(pri:{0} is Masked by pri:{1}", priority, data_bus_reserved_priority );
			return false;
		    }
		}
	    }
	    return true;
	}
	virtual public void postMethodForWinner(SchedBuf winner) // This method is used for each scheduler to check information about winner before issuing request
	{
	    return;
	}
	virtual public void calculate_priority()
	{
	    return;
	}
	virtual public int getPriority(SchedBuf buf )
	{
	    return 0;
	}
	virtual public int getPriority(int id)
	{
	    return 0;
	}
	/* HWA CODE END */

        // Override this for other algorithms
        virtual public void Tick()
        {
            mem.Tick();
#if PACKETDUMP
	    Console.WriteLine("In sched.tick");
#endif
	    /* HWA CODE Comment Out */
	    /*
            SchedBuf winner = null;
	    */
	    /* HWA Code Comment Out End */
	    /* HWA CODE */
	    winner = null;
	    schedMaskPrepare();
	    /* HWA CODE END */		    
            for(int i=0;i<buf.Length;i++)
            {
#if PACKETDUMP
		Console.WriteLine("buf_valid = {0}, buf_busy = {1}, buf_morecommand = {2}, buf num = {3}",buf[i].Valid, buf[i].moreCommands, buf[i].Busy);
#endif
//		    if( buf[i].Valid && buf[i].moreCommands)
//		    {
//			int req_id = buf[i].mreq.request.requesterID;
//			int bank_id = buf[i].mreq.bank_index;
//			Console.WriteLine("SchedBuf{0}/{6}, pid:{1}, bank_id:{4}, pr:{2}, rowhit:{3}, marked:{5},, address={7:X}", i, req_id, 0, buf[i].IsRowBufferHit, bank_id, buf[i].marked, chan.mem_id, buf[i].mreq.request.address);
//		    }
 
                if(buf[i].Valid && buf[i].moreCommands && !buf[i].Busy)

                {
                    bool DBAvailable = buf[i].IsWrite ? (chan.writeRequests < chan.maxWrites) : (chan.readRequests < chan.maxReads);
#if PACKETDUMP
                    Console.WriteLine("in scheduler, DB_avail = {0}, at buffer location {1}, iswrite = {2}",DBAvailable,i,buf[i].IsWrite);
#endif
		    /* HWA CODE */
//                    if(DBAvailable && mem.RequestCanIssue(buf[i]))
                    if(DBAvailable && mem.RequestCanIssue(buf[i]) && schedMaskCheck(buf[i]))
		    	/* HWA CODE END */
		    {
			/* HWA CODE */
			SchedBuf winner_bak = winner;
			/* HWA CODE END */
                        winner = Pick(winner,buf[i]);

			/* HWA CODE */
			if( winner != null )
			{
			    if( winner != buf[i] )
			    {
				buf[i].wait_num++;
//				if( buf[i].mreq.request.requesterID == 17 )
//				    Console.WriteLine("WinnerID:{0}", winner.mreq.request.requesterID );
			    }
			    else if( winner_bak != null )
				winner_bak.wait_num++;
			}
			/* HWA CODE END */
		    }
                }
            }

//	    if( winner != null )
//	    Console.WriteLine("ch:{2}, pid:{0}, bufid:{1}, selected-pre", winner.mreq.request.requesterID, winner.index, chan.mem_id );

	    if( winner != null )
		if( !schedResultMaskChk(winner) ) winner = null;  // 

            if(winner != null)
            {
//		Console.WriteLine("ch:{2}, pid:{0}, bufid:{1}, selected", winner.mreq.request.requesterID, winner.index, chan.mem_id );
                if(winner.whenStarted == ulong.MaxValue)
                    winner.whenStarted = Simulator.CurrentRound;
                if(winner.Urgent)
                    Simulator.stats.DRAMUrgentCommandsPerSrc[winner.mreq.request.requesterID].Add();
                Simulator.stats.DRAMCommandsPerSrc[winner.mreq.request.requesterID].Add();

		postMethodForWinner(winner);

                mem.IssueCommand(winner);
                if(!winner.moreCommands && winner.marked)
                    MarkCompleted(winner);
                chan.lastBankActivity[winner.mreq.rank_index,winner.mreq.bank_index] = Now;
                lastIssue = Now;
		/* HWA CODE */
		if( !winner.moreCommands )
		{
		    if( Simulator.network.nodes[winner.mreq.request.requesterID].cpu.is_HWA() )
			chan.HWAUnIssueRequests--;
		    chan.unIssueRequestsPerCore[winner.mreq.request.requesterID]--;
		    if( !winner.mreq.isWrite )
			chan.unIssueReadRequestsPerCore[winner.mreq.request.requesterID]--;
		    Simulator.QoSCtrl.bw_increment(winner.mreq.request.requesterID,chan.mem_id,chan.id);
		    Simulator.QoSCtrl.mem_req_issue(winner.mreq.request.requesterID, winner.mreq.request.address, chan.mem_id);
		}
		/* HWA CODE END */
            }
        }

        virtual public void MarkCompleted(SchedBuf buf)
        {
        }

        public bool IsCurrentlyRequested(ulong pageIndex)
        {
            for(int i=0;i<buf.Length;i++)
            {
                if(buf[i].Valid && buf[i].moreCommands && buf[i].mreq.shift_row == pageIndex)
                    return true;
            }
            return false;
        }

        public int NumSameRequests(ulong pageIndex)
        {
            int reqs = 0;
            for(int i=0;i<buf.Length;i++)
            {
                if(buf[i].Valid && buf[i].moreCommands && buf[i].mreq.shift_row == pageIndex)
                    reqs++;
            }
            return reqs;
        }
    }
}
