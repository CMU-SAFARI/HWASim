//#define DEBUG
//#define MSHRTHROTTLEDEBUG

using System;
using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Diagnostics;

namespace ICSimulator
{
    public class CPU
    {
        Node m_n;

        public Node node { get { return m_n; } }
        public int ID { get { return m_ID; } }
        public int GID { get { return m_group; } }
        public int groupCount { get { return group_count; } }

        public int windowFree { get { return m_ins.windowFree; } }
        //public Hashtable addr_l2m = new Hashtable();

        InstructionWindow m_ins;
        ulong m_last_retired;

        struct MSHR
        {
            public bool valid;
            public ulong block;
            public bool write;
            public bool pending_write;
            public ulong reqTime;
            public ulong addr;
        }

        int mshrs_free;
        MSHR[] m_mshrs;

        Trace m_trace;
        bool m_trace_valid; // current record valid?

        int m_ID, m_group; //, m_thdID;
        bool m_is_GPU;
        MemoryCoalescing m_MemoryCoalescing;

	/* HWA CODE */
	bool m_is_HWA;
	public ulong deadLine;
	public ulong deadLineCnt;
	public ulong deadLineReq;
	public ulong deadLineReqCnt;
	public ulong deadLineReqFetchCnt;
	public ulong deadLinePassNum;
	public ulong hwaStartOffset;
	bool deadLineMeet;
        public bool is_HWA() {return m_is_HWA;}
	int preCheck_num;

	bool deadLineFromTrace_f = false;
	bool deadLineMulti_f = false;
	uint deadLineMultiNum = 0;
	ulong[] deadLineMultiList;
	ulong[] deadLineReqMultiList;
	uint deadLineMultiCnt = 0;
	public double emergentTh;
	/* HWA CODE END */

        int group_count;

        static Syncer m_sync;

        public bool m_stats_active;
        ulong m_active_ret;


        public bool is_GPU() {return m_is_GPU;}

        public ulong ICount { get { return m_active_ret; } }
        bool m_done;

        //stats
        public ulong outstandingReqsNetwork = 0;
        public ulong outstandingReqsNetworkCycle;
        public ulong outstandingReqsMemory = 0;
        public ulong outstandingReqsMemoryCycle;

        ulong alone_t;

        public CPU(Node n)
        {
            m_n = n;
            m_ID = m_n.coord.ID;
            String filename = Simulator.network.workload.getFile(m_ID);
            //TODO: [Check this] detect if a node is a GPU here
            if(filename.Contains("GAME") || filename.Contains("BENCH")) // GPU
            {
            Console.WriteLine("core:{1} is a GPU", Simulator.CurrentRound, m_ID);
                m_ins = new GPUWindow(this);
                //m_sets = null;
                m_is_GPU = true;
                m_MemoryCoalescing = new MemoryCoalescing();
		/* HWA CODE */
		m_is_HWA = false; 
            }else if( filename.Contains("HWA")) // Accelerator
	       {
		 m_is_GPU = false;
		 m_is_HWA = true;
		 m_ins = new InstructionWindow(this);
		 m_MemoryCoalescing = null;
	       }
	    /* HWA CODE END */
            else // CPU
            {

                m_is_GPU = false;
                m_ins = new InstructionWindow(this);
                m_MemoryCoalescing = null;
		/* HWA CODE */
		m_is_HWA = false;
		/* HWA CODE END */
            }
            if (m_sync == null)
                m_sync = new Syncer();

            m_group = Simulator.network.workload.getGroup(m_ID);
            //m_thdID = Simulator.network.workload.getThread(m_ID);
            group_count = Simulator.network.workload.GroupCount;

            openTrace(m_is_GPU);
            m_trace_valid = false;

            // Allow different mshr size for different CPU
            int mshrSize = Config.mshrs;

            string [] mList = Config.mshrSizeList.Split(',');
            if (mList.Length == Config.N)
                mshrSize = Convert.ToInt32(mList[m_n.coord.ID]);
            else if (mList.Length > 1 && mList.Length < Config.N)
                throw new Exception(String.Format("Invalid mshrs list. Need to match # of nodes: {0}", Config.N));

            Console.WriteLine("Node {0} mshrs {1}", m_n.coord.ID, mshrSize);
            m_mshrs = new MSHR[mshrSize];
            if (Config.mshrsList == null)
                Config.mshrsList = new int[Config.N];
            Config.mshrsList[m_n.coord.ID] = mshrSize;

            for (int i = 0; i < mshrSize; i++)
            {
                m_mshrs[i].valid = false;
                m_mshrs[i].block = 0;
                m_mshrs[i].write = false;
                m_mshrs[i].pending_write = false;
                m_mshrs[i].addr = 0;
                m_mshrs[i].reqTime = 0;
            }
            mshrs_free = mshrSize;

            m_stats_active = true;
            alone_t = ulong.MaxValue;

	    /* HWA CODE */
	    deadLine = 0;
	    deadLineCnt = (ulong)Config.warmup_cyc;
	    deadLineReq = 0;
	    deadLineReqCnt = 0;
	    deadLineReqFetchCnt = 0;
	    deadLinePassNum = 0;
	    hwaStartOffset = 0;

	    if( m_is_HWA || m_is_GPU )
		{
		    string [] deadLineList = Config.hwaDeadLineList.Split(',');
		    if (deadLineList.Length == Config.N)
			deadLine = Convert.ToUInt64(deadLineList[m_n.coord.ID]);
		    else if (deadLineList.Length > 1 && deadLineList.Length < Config.N)
			throw new Exception(String.Format("Invalid deadline list. Need to match # of nodes: {0}", Config.N));

		    Console.WriteLine("Node {0} deadLine {1}", m_n.coord.ID, deadLine );

		    string [] deadLineReqCntList = Config.hwaDeadLineReqCntList.Split(',');
		    if (deadLineReqCntList.Length == Config.N)
			deadLineReq = Convert.ToUInt64(deadLineReqCntList[m_n.coord.ID]);
		    else if (deadLineReqCntList.Length > 1 && deadLineReqCntList.Length < Config.N)
			throw new Exception(String.Format("Invalid deadline list. Need to match # of nodes: {0}", Config.N));

		    Console.WriteLine("Node {0} deadLineReq {1}", m_n.coord.ID, deadLineReq );
		    if( deadLine == 0 )
		    {
			deadLineFromTrace_f = true;
		    }

		    string [] hwaStartOffsetList = Config.hwaStartOffsetList.Split(',');
		    if (hwaStartOffsetList.Length == Config.N)
			hwaStartOffset = Convert.ToUInt64(hwaStartOffsetList[m_n.coord.ID]);
		    else if (hwaStartOffsetList.Length > 1 && hwaStartOffsetList.Length < Config.N)
			throw new Exception(String.Format("Invalid deadline list. Need to match # of nodes: {0}", Config.N));

		    deadLineCnt += hwaStartOffset;

		    Console.WriteLine("Node {0} startOffset {1}", m_n.coord.ID, hwaStartOffset );

		    string [] emergentThList = Config.hwaEmergentThList.Split(',');
		    if (emergentThList.Length == Config.N)
			emergentTh = Convert.ToDouble(emergentThList[m_n.coord.ID]);
//		    else if (emergentThList.Length > 1 && emergentThList.Length < Config.N)
		    else
			emergentTh = -1.0;

		    Console.WriteLine("Node {0} emergent Threshold {1}", m_n.coord.ID, emergentTh );

		    string [] deadLineMultiStartList = Config.hwaDeadLineMultiStartList.Split(',');
		    string [] deadLineMultiNumList = Config.hwaDeadLineMultiNumList.Split(',');
		    string [] deadLineMultiAllList = Config.hwaDeadLineMultiAllList.Split(',');
		    string [] deadLineReqMultiAllList = Config.hwaDeadLineReqMultiAllList.Split(',');
		    if(( deadLineMultiStartList.Length == Config.N) &&
		       ( deadLineMultiNumList.Length == Config.N ))
		    {	
			uint deadLineMultiStartID = Convert.ToUInt32(deadLineMultiStartList[m_n.coord.ID]);
			deadLineMultiNum = Convert.ToUInt32(deadLineMultiNumList[m_n.coord.ID]);
			if( deadLineMultiNum > 1 )
			{
			    deadLineMulti_f = true;
			    deadLineMultiList = new ulong[deadLineMultiNum];
			    deadLineReqMultiList = new ulong[deadLineMultiNum];
			    for( uint d_line_id = deadLineMultiStartID; d_line_id < deadLineMultiStartID + deadLineMultiNum; d_line_id++ )
			    {
				deadLineMultiList[d_line_id-deadLineMultiStartID] = Convert.ToUInt64(deadLineMultiAllList[d_line_id]);
				deadLineReqMultiList[d_line_id-deadLineMultiStartID] = Convert.ToUInt64(deadLineReqMultiAllList[d_line_id]);
			    }
			    deadLine = deadLineMultiList[0];
			    deadLineReq = deadLineReqMultiList[0];
			    deadLineMultiCnt = 0;
			}
		    }
		    
		}
	    /* HWA_CODE END */
        }

        public bool Finished
        {
            get
            {
                if (m_trace == null)
                    return true;
                else if (Config.trace_wraparound)
                    return m_done;
                else if (m_trace != null)
                    return m_trace.EOF;
                else
                    return true;
            }
        }

        public bool Livelocked
        { get { return (Simulator.CurrentRound - m_last_retired) > Config.livelock_thresh; } }

        void openTrace(bool isGPU)
        {
            if (Config.bochs_fe)
            {
                m_trace = new TraceBochs(m_ID);
                return;
            }

            string tracefile = Simulator.network.workload.getFile(m_ID);

            if (tracefile == "null")
            {
                m_trace = null;
		Console.WriteLine("tracefile at {0} is null",m_ID);
                return;
            }
            if (isGPU)
                m_trace = new TraceFile_AMD_GPU(tracefile, m_group);
            else if (tracefile == "synth")
                m_trace = new TraceSynth(m_group);
            else if (tracefile.EndsWith(".gz"))
	    {
		Console.WriteLine("HOGE:{0}",tracefile);
		if( tracefile.Contains("HWA") && ( Config.sched.hwaBankCheckNum > 0 ))
		    m_trace = new TraceFile_Old_Bank_Check(tracefile, m_group, Config.sched.hwaBankCheckNum, Config.sched.is_chunk_base);
		else if( tracefile.Contains("HWA") && ( Config.sched.qosPreDstCheckNum > 0 ))
		    m_trace = new TraceFile_Old_EnPreCheck(tracefile, m_group);
		else if( tracefile.Contains("NPB"))
		    m_trace = new TraceFile_Old_withPC(tracefile, m_group);
		else
		    m_trace = new TraceFile_Old(tracefile, m_group);
	    }
            else if (tracefile.EndsWith(".bin"))
                m_trace = new TraceFile_Old_Scalable(tracefile, m_group);
            else
                m_trace = new TraceFile_New(tracefile, m_group);

            if (Config.randomize_trace > 0)
            {
                ulong start = (ulong)(Simulator.rand.NextDouble() * Config.randomize_trace);

                Console.WriteLine("CPU {0} starting at insn {1}", m_ID, start);
                Simulator.stats.skipped_insns_persrc[m_ID].Add(start);
                m_trace.seek(start);
                m_ins.SeekLog(start);
            }
        }

        public void receivePacket(CachePacket p)
        {
            if (p.cb != null) p.cb();
        }

        void doStats(ulong retired)
        {
            Simulator.stats.every_insns_persrc[m_ID].Add(retired);
            Simulator.network._cycle_insns += retired;

            if (Simulator.Warming)
            {
                Simulator.stats.warming_insns_persrc[m_ID].Add(retired);
                return;
            }

            if(!m_stats_active) return;

            Simulator.stats.mshrs.Add(mshrs_free);
            Simulator.stats.mshrs_persrc[m_ID].Add(mshrs_free);

            m_active_ret += retired;

            if (alone_t == ulong.MaxValue)
                alone_t = m_ins.oldestT;
            ulong alone_cyc = m_ins.oldestT - alone_t;
            alone_t = m_ins.oldestT;

            Simulator.stats.insns_persrc[m_ID].Add(retired);
            Simulator.stats.insns_persrc_period[m_ID].Add(retired);
            Simulator.stats.active_cycles[m_ID].Add();
            Simulator.stats.active_cycles_alone[m_ID].Add(alone_cyc);


            if (Simulator.CurrentRound % (ulong)100000 == 0)// && Simulator.CurrentRound != 0)
            {
                Console.WriteLine("Processor {0}: {1} ({2} outstanding)",
                                  m_ID, m_ins.totalInstructionsRetired,
                                  m_ins.outstandingReqs);
#if DEBUG
                Console.WriteLine("-- outstanding:");
                foreach (MSHR m in m_mshrs)
                {
//                    if (m.block != null) Console.Write(" {0:X}", m.block.Block);
                    if (m.block != 0) Console.Write(" {0:X}", m.block);
                }
                Console.WriteLine();
#endif
            }

            bool windowFull = m_ins.isFull();
            bool nextIsMem = (m_trace.type == Trace.Type.Rd || m_trace.type == Trace.Type.Wr);
            bool noFreeMSHRs = true;
            for (int i = 0; i < m_mshrs.Length; i++)
            {
                if (!m_mshrs[i].valid)
                    noFreeMSHRs = false;
            }

            // any stall: either (i) window is full, or (ii) window is not full
            // but next insn (LD / ST) can't be issued
            bool stall = windowFull || (nextIsMem && noFreeMSHRs);

            // MSHR stall: window not full, next insn is memory, but we have no free MSHRs
            bool stallMem = !windowFull && (nextIsMem && noFreeMSHRs);
/*
            // promise stall: MSHR stall, and there is a pending eviction (waiting for promise) holding
            // an mshr
            bool stallPromise = stallMem && pendingEvict;
            */

            if (stall)
                Simulator.stats.cpu_stall[m_ID].Add();
            if (stallMem)
                Simulator.stats.cpu_stall_mem[m_ID].Add();

            /*
            if (stallPromise)
                Simulator.stats.promise_wait[m_ID].Add();
                */
        }

        bool advanceTrace()
        {
            if (m_trace == null)
            {
                m_trace_valid = false;
                return false;
            }
	    /* HWA CODE */
	    if( m_is_HWA )
	    {
		if( Simulator.CurrentRound < (ulong)Config.warmup_cyc + hwaStartOffset )
		{
		    m_trace_valid = false;
		    return false;
		}
		if( deadLine == 0 ) // initial state
		{
		    m_trace_valid = m_trace.getDeadline();
		    deadLine = m_trace.deadline;
		    deadLineReq = (ulong)m_trace.req_cnt;
		    Console.WriteLine("HWA {0}: new(initial) deadline {1}, req {2}", m_ID, deadLine, deadLineReq );
		    Simulator.QoSCtrl.deadline_update(m_ID,deadLine,deadLineReq);
		    m_trace_valid = false;
		}
		if( deadLineReqFetchCnt >= deadLineReq )
		{
		    if( deadLineReqCnt < deadLineReq )
		    {
			m_trace_valid = false;
			return false;
		    }else if( ( Simulator.CurrentRound - deadLineCnt ) < deadLine )
		    {
			m_trace_valid = false;
			if( !deadLineMeet )
			{
			    deadLineMeet = true;
			    Console.WriteLine("HWA {0}: met deadline {2} in {1}", m_ID, Simulator.CurrentRound, deadLineCnt + deadLine );
			}
			return false;
		    }else
		    {
			if( !deadLineMeet )
			{
			    Console.WriteLine("HWA {0}: missed deadline {2} in {1}", m_ID, Simulator.CurrentRound, deadLineCnt + deadLine );			    
			}
			deadLineReqCnt = 0;
			deadLineReqFetchCnt = 0;
			deadLineCnt = Simulator.CurrentRound;
			deadLineMeet = false;
			deadLinePassNum++;
			if( deadLineFromTrace_f )
			{
			    m_trace_valid = m_trace.getDeadline();
			    if (!m_trace_valid)
			    {
				m_trace.rewind();
				m_trace_valid = m_trace.getDeadline();
			    }
			    deadLine = m_trace.deadline;
			    deadLineReq = (ulong)m_trace.req_cnt;
			    Console.WriteLine("HWA {0}: new deadline {1}, req {2}", m_ID, deadLine, deadLineReq );
			    Simulator.QoSCtrl.deadline_update(m_ID,deadLine,deadLineReq);
			    m_trace_valid = false;
			}
			if( deadLineMulti_f )
			{
			    deadLineMultiCnt++;
			    if( deadLineMultiCnt == deadLineMultiNum )
				deadLineMultiCnt = 0;
			    deadLine = deadLineMultiList[deadLineMultiCnt];
			    deadLineReq = deadLineReqMultiList[deadLineMultiCnt];
			}
			/*
			if( Config.sched.qosPreDstCheckNum > 0 ) // Because this function is useless, it is commented out
			{
			    int check_num;
			    int get_num = 0;
			    bool get_flag;
			    ulong addr, s_row;
			    int mem_idx, ch_idx, rank_idx, bank_idx, row_idx;

			    check_num = Config.sched.qosPreDstCheckNum;
			    
			    while( get_num < check_num )
			    {
				get_flag = m_trace.preCheck(out addr);
				if( !get_flag )
				{
				    m_trace.rewind();
				    get_flag = m_trace.preCheck(out addr);
				}
				get_num++;
//				MemoryRequest.mapAddr(addr>>Config.cache_block, out s_row, out mem_idx, out ch_idx, out rank_idx, out bank_idx, out row_idx );
				MemoryRequest.mapAddr(ID,addr>>Config.cache_block, out s_row, out mem_idx, out ch_idx, out rank_idx, out bank_idx, out row_idx );
				Simulator.QoSCtrl.issue_request(m_ID,mem_idx,ch_idx,true);
				preCheck_num++;

			    }
			    Simulator.QoSCtrl.adjust_each_epoch();
			}
			*/
			m_trace_valid = m_trace.getNext();			
			if( !m_trace_valid )
			{
			    m_trace.rewind();
			    m_trace_valid = m_trace.getNext();
			}
//			if( m_trace_valid )
//			    Console.WriteLine("HWA {0}: start 1st transfer(addr:{1:x}, time:{2}", m_ID, m_trace.address, Simulator.CurrentRound );

			if( m_trace_valid )
			    Simulator.QoSCtrl.schedule_each_deadline(m_ID, m_trace);
			else
			    Simulator.QoSCtrl.schedule_each_deadline(m_ID, null);

			return m_trace_valid;
		    }
		}
	    }
	    /* HWA CODE End */
//           if(Simulator.CurrentRound > 700000 && m_ID ==15)
//            Console.WriteLine("cycle:{0} in advanceTrace(), trace valid = {1} ", Simulator.CurrentRound, m_trace_valid);
 
            if (!m_trace_valid)
                m_trace_valid = m_trace.getNext();
//        if(Simulator.CurrentRound > 700000 && m_ID ==15)
//            Console.WriteLine("cycle:{0} in advanceTrace() -- after getNext(), trace valid = {1} ", Simulator.CurrentRound, m_trace_valid);

            if (Config.trace_wraparound) // for GPU, wraparound has to be defined
            {
                if (!m_trace_valid)
                {
		    if( m_is_GPU )
		    {
			if( ( Simulator.CurrentRound - deadLineCnt ) < deadLine )
			{
			    m_trace_valid = false;
			    if( !deadLineMeet )
			    {
				deadLineMeet = true;
				Console.WriteLine("GPU {0}: met deadline {2} in {1} ins:{3}", m_ID, Simulator.CurrentRound, deadLineCnt + deadLine, deadLineReqCnt );
			    }
			    return false;
			}
			else
			{
			    if( !deadLineMeet )
			    {
				Console.WriteLine("GPU {0}: missed deadline {2} in {1} ins:{3}", m_ID, Simulator.CurrentRound, deadLineCnt + deadLine, deadLineReqCnt );			    
			    }
			    deadLineReqCnt = 0;
			    deadLineCnt = Simulator.CurrentRound;
			    deadLineMeet = false;
			    deadLinePassNum++;
			    m_trace.rewind();
			    m_trace_valid = m_trace.getNext();
			    return m_trace_valid;
			}
		    }

                    m_trace.rewind();
                    m_trace_valid = m_trace.getNext();

		    m_stats_active = false;

		    Console.WriteLine("trace is wrapped around, trace_valid:{0}\n", m_trace_valid);
//		    m_done = true;

                }

                if (Simulator.network.finishMode == Network.FinishMode.app &&
                        m_active_ret >= Config.insns)
                {
                    m_stats_active = false;
                    m_done = true;
                }
            }
            return m_trace_valid;
        }

        bool canIssueMSHR(ulong addr)
        {
            if(!m_is_GPU)
            {
                ulong block = addr >> Config.cache_block;
    
                for (int i = 0; i < m_mshrs.Length; i++)
                    if (m_mshrs[i].block == block)
                        return true;
    
                // Throttle by decreasing # of avaiable mshrs
                if (Config.mshrsWatermark == null)
                    Config.mshrsWatermark = new int[Config.N];
    #if MAKEDEBUG
                if (mshrs_free < Config.mshrsWatermark[m_n.coord.ID])
                    Console.WriteLine("\n### Free mshrs: {0} watermark: {1}", mshrs_free, Config.mshrsWatermark[m_n.coord.ID]);
    #endif
                return mshrs_free > Config.mshrsWatermark[m_n.coord.ID];
            }
            else //GPU case
                return true;
        }
	/* HWA CODE */
//        void issueReq(Request req)
        void issueReq(Request req, int windowID)
	/* HWA CODE END */
        {
        // Issue Req from a CPU node
            if(!m_is_GPU)
            {
		//Console.WriteLine("core:{3} issueReq: block {0:X}, write {1} at cyc {2}", req.blockAddress, req.write, Simulator.CurrentRound, m_ID);

            for (int i = 0; i < m_mshrs.Length; i++)
                if ((m_mshrs[i].block == req.blockAddress) && m_mshrs[i].valid )
                {
                    if (req.write && !m_mshrs[i].write)
                        m_mshrs[i].pending_write = true;

//                    if (m_ID == 0) Console.WriteLine("P0 issueReq: found req in MSHR {0}", i);

                    return;
                }

//            Console.WriteLine("In issueReq, before resolving MSHR");
            int mshr = -1;
            for (int i = 0; i < m_mshrs.Length; i++)
                if (!m_mshrs[i].valid)
                {
                    mshr = i;
                    break;
                }
            Debug.Assert(mshr != -1);

            mshrs_free--;

            m_mshrs[mshr].valid = true;
            m_mshrs[mshr].block = req.blockAddress;
            m_mshrs[mshr].write = req.write;

            // Use these to determine if any mem req returns while there are other L2 misses out there
            m_mshrs[mshr].reqTime = Simulator.CurrentRound;
            m_mshrs[mshr].addr = req.address;

//	    if( m_is_HWA ) Console.WriteLine("ID:{2} Issue Req addr:{0:X}, write:{1}", req.address, req.write,ID);
//            Console.WriteLine("In issueReq, after resolving MSHR");
            _issueReq(mshr, req.address, req.write, Simulator.CurrentRound, windowID);
            }
        // Issue Req from a GPU node
            else
            {
                int mem_slice = Simulator.controller.mapMC(req.requesterID, req.address >> Config.cache_block);
                m_MemoryCoalescing.issueReq(mem_slice,req, delegate() { reqDone(req.address, -1, req.write);});
            }
        }
	/* HWA CODE */
//        void _issueReq(int mshr, ulong addr, bool write, ulong reqTime )
        void _issueReq(int mshr, ulong addr, bool write, ulong reqTime, int windowID )
	/* HWA CODE END */
        {
//            Console.WriteLine("In _issueReq, before accessing the cache");
            bool L1hit = false, L1upgr = false, L1ev = false, L1wb = false;
            bool L2access = false, L2hit = false, L2ev = false, L2wb = false, c2c = false;

            Simulator.network.cache.access(m_ID, addr, write,
                    delegate() { reqDone(addr, mshr, write); },
                    out L1hit, out L1upgr, out L1ev, out L1wb, out L2access, out L2hit, out L2ev, out L2wb, out c2c);

	    if(( m_ID == 0 ) && ( addr == 0 ))
	    {
		Console.WriteLine("cacheAccess l1_hit:{0},l2_acc:{1},l2_hit:{2}", L1hit,L2access,L2hit);
	    }
            // TODO: buggy code -> causes exception with dup key in the hash table
            /*if (L2access && L2hit)
            {
                for (int i = 0; i < m_mshrs.Length; i++)
                {
                    if (m_mshrs[i].valid && i != mshr)
                    {
                        if (m_mshrs[i].reqTime <  reqTime)
                        {
                            Simulator.stats.L2_potential_MLP[m_ID].Add();
                            break;
                        }
                    }
                }
            }*/

//            Console.WriteLine("In _issueReq:after accessing the cache");
            if (!L1hit)
            {
                 Simulator.network._cycle_L1_misses++;
		         Simulator.controller.L1misses[m_ID]++;
		         Simulator.stats.L1misses[m_ID].Add();
            }


            if (m_stats_active)
            {
                Simulator.stats.L1_accesses_persrc[m_ID].Add();

                if (L1hit)
                    Simulator.stats.L1_hits_persrc[m_ID].Add();
                else
                {
                    Simulator.stats.L1_misses_persrc[m_ID].Add();
                    Simulator.stats.L1_misses_persrc_period[m_ID].Add();
                }

                if (L1upgr)
                    Simulator.stats.L1_upgr_persrc[m_ID].Add();
                if (L1ev)
                    Simulator.stats.L1_evicts_persrc[m_ID].Add();
                if (L1wb)
                    Simulator.stats.L1_writebacks_persrc[m_ID].Add();
                if (c2c)
                    Simulator.stats.L1_c2c_persrc[m_ID].Add();

                if (L2access)
                {
                    Simulator.stats.L2_accesses_persrc[m_ID].Add();

                    if (L2hit)
                        Simulator.stats.L2_hits_persrc[m_ID].Add();
                    else
                        Simulator.stats.L2_misses_persrc[m_ID].Add();

                    if (L2ev)
                        Simulator.stats.L2_evicts_persrc[m_ID].Add();
                    if (L2wb)
                        Simulator.stats.L2_writebacks_persrc[m_ID].Add();
                }

		/* HWA CODE */
		if(( L2access && !L2hit && !write ) || ( m_is_HWA && !write ))
		    m_ins.setDramReq(windowID);

		if( preCheck_num == 0 )
		{
		    ulong s_row;
		    int mem_idx, ch_idx, rank_idx, bank_idx, row_idx;
//		    MemoryRequest.mapAddr(addr>>Config.cache_block, out s_row, out mem_idx, out ch_idx, out rank_idx, out bank_idx, out row_idx );
		    MemoryRequest.mapAddr(ID,addr>>Config.cache_block, out s_row, out mem_idx, out ch_idx, out rank_idx, out bank_idx, out row_idx );
		    Simulator.QoSCtrl.issue_request(m_ID,mem_idx,ch_idx,false);
		}
		else
		    preCheck_num--;
		     
		/* HWA CODE END */
            }
        }

        void reqDone(ulong addr, int mshr, bool write)
        {
            m_ins.setReady(addr, write);
//	    if(m_is_HWA && ( m_ID == 17 ))
//	    if(( m_ID == 0 ) || ( m_ID == 17 ))
//		Console.WriteLine("{1}-Lat:{0}", Simulator.CurrentRound - m_mshrs[mshr].reqTime,m_ID );
            if(!m_is_GPU)
            {
                if (!write && m_mshrs[mshr].pending_write)
                {
                    m_mshrs[mshr].pending_write = false;
                    m_mshrs[mshr].write = true;
    
		    /* HWA CODE */
//                    _issueReq(mshr, m_mshrs[mshr].block << Config.cache_block, true, Simulator.CurrentRound);
                    _issueReq(mshr, m_mshrs[mshr].block << Config.cache_block, true, Simulator.CurrentRound,0);
		    /* HWA CODE END */
                }
                else
                {
                    /*
                    ulong myAddr = m_mshrs[mshr].addr;
                    ulong myReqTime = m_mshrs[mshr].reqTime;
                    bool myl2m;
                    if (addr_l2m.ContainsKey(myAddr))
                    {
                        myl2m = (bool) addr_l2m[myAddr];
                        addr_l2m.Remove(myAddr);
                    }
                    else
                        throw new Exception(String.Format("unable to find the L2 status in the hashtable {0}.", myAddr));
    
    
                    if (myl2m == false)
                    {
                        //Simulator.stats.L2_potential_MLP[m_ID].Add();
                        for (int i = 0; i < m_mshrs.Length; i++)
                        {
                            if (m_mshrs[i].valid && i != mshr)
                            {
                                if (m_mshrs[i].reqTime <  myReqTime)
                                {
                                    //Simulator.stats.L2_potential_MLP[m_ID].Add();
                                    break;
                                }
                            }
                        }
                    }*/
    
                    m_mshrs[mshr].valid = false;
                    m_mshrs[mshr].block = 0;
                    m_mshrs[mshr].write = false;
                    m_mshrs[mshr].pending_write = false;
    
                    mshrs_free++;
                }
            }
        }

        public bool doStep()
        {
            if (m_trace == null) {return true;}

	    /* HWA CODE */
//	    if( !Simulator.QoSCtrl.is_initialized )
//		Simulator.QoSCtrl.schedule_quantum();

            if(m_is_GPU)
                m_MemoryCoalescing.doStep();


            int syncID;

//            Console.WriteLine("core:{1} step: cyc {0}", Simulator.CurrentRound, m_ID);
            ulong retired;
            if(m_is_GPU)
                retired = (ulong)m_ins.retire(Config.proc.GPUinstructionsPerCycle);
            else
                retired = (ulong)m_ins.retire(Config.proc.instructionsPerCycle);

	    /* HWA CODE */
	    if ( m_is_HWA )
		deadLineReqCnt += retired;
	    /* HWA_CODE END */
	    
            if (retired > 0)
                m_last_retired = Simulator.CurrentRound;

            if (!m_trace_valid)
                m_trace_valid = advanceTrace(); // doStats needs to see the next record

//        if(Simulator.CurrentRound > 650000 && m_ID ==15)
//            Console.WriteLine("core:{1} after advancetrace in DOSTEP: cyc {0}, ID = {1}, trace_valid = {2}, retiring {3} instructions, isfull = {4}", Simulator.CurrentRound, m_ID, m_trace_valid, retired, m_ins.isFull());



//            Console.WriteLine("core:{1} before stat: cyc {0}", Simulator.CurrentRound, m_ID);
            doStats(retired);

            if (m_ins.isFull()) return true;

            bool done = false;
            int nIns = Config.proc.instructionsPerCycle;
            if(m_is_GPU)
                nIns = Config.proc.GPUinstructionsPerCycle;
            else if(Config.CPU == false)
                done = true;


            int nMem = 1;

//        if(Simulator.CurrentRound > 650000 && m_ID ==15)
//            Console.WriteLine("core:{1} before loop: cyc {0}, ID = {1} done = {2}, numIns = {3}, isFull = {4}", Simulator.CurrentRound, m_ID, done, nIns, m_ins.isFull());
            while (!done && nIns > 0 && !m_ins.isFull())
            {
//          if(Simulator.CurrentRound > 700000 && m_ID ==15)
//            Console.WriteLine("cycle:{0} in loop before advance trace, trace valid = {1} ", Simulator.CurrentRound, m_trace_valid);
                if (!m_trace_valid)
//           if(Simulator.CurrentRound > 700000 && m_ID ==15)
//            Console.WriteLine("cycle:{0} in loop after advance trace, trace valid = {1} ", Simulator.CurrentRound, m_trace_valid);
                   m_trace_valid = advanceTrace();
                if (!m_trace_valid)
                    return false;

                if (m_trace.type == Trace.Type.Pause) // when execution-driven, source has nothing to give
                {
                    m_trace_valid = false;
                    return true;
                }

                if (m_trace.type == Trace.Type.Sync)
                {
                    // `from' field: translate referrent from thd ID to physical CPU id
                    syncID = Simulator.network.workload.mapThd(m_group, m_trace.from);
                }
                else
                    syncID = m_trace.from;

                switch (m_trace.type)
                {
                case Trace.Type.Rd:
                case Trace.Type.Wr:
                    if (nMem == 0 || !canIssueMSHR(m_trace.address))
                    {
                        done = true;
                        break;
                    }
                    nMem--;
                    nIns--;
		    if( m_is_GPU )
		    {
			deadLineReqCnt++;
		    }

                    ulong addr = m_trace.address;
                    bool isWrite = m_trace.type == Trace.Type.Wr;
                    bool inWindow = m_ins.contains(addr, isWrite);
		    int windowID = m_ins.next;
		    
                    Request req = inWindow ? null : new Request(m_ID, addr, isWrite);
		    /* HWA CODE */
		    if( m_is_HWA )
		    {
			deadLineReqFetchCnt++;
//			    if( !inWindow ) req.setDeadLine( deadLine, deadLineCnt, deadLineReq, deadLineReqCnt );
		    }
		    /* HWA CODE END */
                    m_ins.fetch(req, addr, isWrite, inWindow);
//        if(Simulator.CurrentRound > 700000 && m_ID ==15)
//            Console.WriteLine("core:{1} in loop - mem: cyc {0}, inWindow = {1}", Simulator.CurrentRound, m_ID, inWindow);
                    if (!inWindow)
                    {
                        if(!m_is_GPU)
                            issueReq(req, windowID);
                        else
                        {
                            req.from_GPU = true;
                            req.memsize = ((TraceFile_AMD_GPU)m_trace).memsize;
                            req.client = ((TraceFile_AMD_GPU)m_trace).client;
                            issueReq(req, windowID);
                        }

                    }
                    m_trace_valid = false;
                    break;

                case Trace.Type.NonMem:
                    if (m_trace.address > 0)
                    {
//            Console.WriteLine("core:{1} in loop - nonmem before fetch: cyc {0}", Simulator.CurrentRound, m_ID);
                        m_ins.fetch(null, InstructionWindow.NULL_ADDRESS, false, true);
                        m_trace.address--;
                        nIns--;
                    }
                    else
                        m_trace_valid = false;

                    break;

                case Trace.Type.Label:
                    if(m_sync.Label(m_ID, syncID, m_trace.address))
                        m_trace_valid = false; // allowed to continue
                    else
                    { // shouldn't ever block...
                        if (m_stats_active)
                            Simulator.stats.cpu_sync_memdep[m_ID].Add();
                        done = true; // blocking: done with this cycle
                    }

                    break;

                case Trace.Type.Sync:
                    if (m_sync.Sync(m_ID, syncID, m_trace.address))
                        m_trace_valid = false;
                    else
                    {
                        if (m_stats_active)
                            Simulator.stats.cpu_sync_memdep[m_ID].Add();
                        done = true;
                    }

                    break;

                case Trace.Type.Lock:
                    //Console.WriteLine("Lock" + ' ' + m_ID.ToString() + ' ' + syncID.ToString() + ' ' + m_trace.address.ToString());
                    if (m_sync.Lock(m_ID, syncID, m_trace.address))
                        m_trace_valid = false;
                    else
                    {
                        if (m_stats_active)
                            Simulator.stats.cpu_sync_lock[m_ID].Add();
                        done = true;
                    }

                    break;

                case Trace.Type.Unlock:
                    //Console.WriteLine("Unlock" + ' ' + m_ID.ToString() + ' ' + syncID.ToString() + ' ' + m_trace.address.ToString());
                    if (m_sync.Unlock(m_ID, syncID, m_trace.address))
                        m_trace_valid = false;
                    else
                    { // shouldn't ever block...
                        if (m_stats_active)
                            Simulator.stats.cpu_sync_lock[m_ID].Add();
                        done = true;
                    }

                    break;

                case Trace.Type.Barrier:
                    //Console.WriteLine("Barrier" + ' ' + m_ID.ToString() + ' ' + syncID.ToString() + ' ' + m_trace.address.ToString());
                    if (m_sync.Barrier(m_ID, syncID, m_trace.address))
                        m_trace_valid = false;
                    else
                    {
                        if (m_stats_active)
                            Simulator.stats.cpu_sync_barrier[m_ID].Add();
                        done = true;
                    }

                    break;
                }

//            Console.WriteLine("core:{1} in loop - at the end: cyc {0}", Simulator.CurrentRound, m_ID);
            }

//            Console.WriteLine("core:{1} after loop: cyc {0}", Simulator.CurrentRound, m_ID);
            return true;
        }

        public ulong get_stalledSharedDelta() { throw new NotImplementedException(); }
    }
}
