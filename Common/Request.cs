using System;
using System.Collections.Generic;
using System.Text;

namespace ICSimulator
{

    public enum MemoryRequestType
    {
        RD,
        DAT,
        WB,
    }

    public class MemoryRequest
    {
        public Request request;
        public MemoryRequestType type;
        public int memoryRequesterID; ///<summary>LLC node</summary>

        public ulong timeOfArrival;
        public bool isMarked;
        public bool isWrite;
        public bool scheduled = false; // has been scheduled and started bank access (or done)

        public ulong creationTime;

		public ulong shift_row;
        public int mem_index;       ///<summary>memory index pertaining to the block address</summary>
        public int channel_index;   ///<summary>channel index pertaining to the block address</summary>
        public int rank_index;      ///<summary>rank index pertaining to the block address</summary>
        public int bank_index;      ///<summary>bank index pertaining to the block address</summary>
        public int row_index;       ///<summary>row index pertaining to the block address</summary>

        public int buf_index=-1;       ///<summary>memory scheduler buffer index</summary>

        public bool from_GPU = false;
        public uint mem_size = 64;   // default = cache-line size

        public bool is_marked = false; // For ATLAS
        
        public Simulator.Ready cb; // completion callback

        public static void mapAddr(ulong block,
                out ulong shift_row, out int mem_index, out int channel_index, out int rank_index, out int bank_index, out int row_index)
        {
            /* row | bank | rank | channel | memctrl | page offset */
//            shift_row = block >> Config.memory.row_bit;
            shift_row = ( block << Config.cache_block ) >> ( Config.memory.row_bit + (int)Math.Ceiling(Math.Log(Config.memory.busWidth,2.0)) );

            mem_index = (int)((shift_row >> Config.memory.mem_bit) & (ulong)(Config.memory.numMemControllers-1));
            //channel_index = (int)((shift_row >> Config.memory.channel_bit) & (ulong)(Config.memory.numChannels-1));
            //channel_index = channelMapping.getChannel((int)((shift_row >> Config.memory.channel_bit),(ulong)(Config.memory.numChannels-1)),req);
            channel_index = (int)((shift_row >> Config.memory.channel_bit) % (ulong)(Config.memory.numChannels));
            Simulator.stats.channelUsed.Add(channel_index);
            rank_index = (int)((shift_row >> Config.memory.rank_bit) & (ulong)(Config.memory.numRanks-1));
            bank_index = (int)((shift_row >> Config.memory.bank_bit) & (ulong)(Config.memory.numBanks-1));
            row_index = (int)(shift_row >> Config.memory.row_bit);
        }

        public static void mapAddr(int id, ulong block,
                out ulong shift_row, out int mem_index, out int channel_index, out int rank_index, out int bank_index, out int row_index)
        {
	    if(( Simulator.network.nodes[id].cpu.is_HWA() && 
		!Simulator.QoSCtrl.schedule_tgt( id, 0, 0 ) && // llclst
		Config.sched.is_llclst_mmap_block_interleave ) ||
	       Config.sched.is_all_mmap_block_interleave )
	    {
		/* row | h_column | bank | rank | channel | memctrl | l_column | bus */
		int bus_bit_lsb = 0;
		int bus_bit_width = (int)Math.Ceiling(Math.Log(Config.memory.busWidth,2.0));
		int l_column_bit_lsb = bus_bit_lsb + bus_bit_width;
		int l_column_bit_width = 3;
		int memctrl_bit_lsb = l_column_bit_lsb + l_column_bit_width;
		int memctrl_bit_width = (int)Math.Ceiling(Math.Log(Config.memory.numMemControllers,2.0));
		int channel_bit_lsb = memctrl_bit_lsb + memctrl_bit_width;
		int channel_bit_width = (int)Math.Ceiling(Math.Log(Config.memory.numChannels,2.0));
		int rank_bit_lsb = channel_bit_lsb + channel_bit_width;
		int rank_bit_width = (int)Math.Ceiling(Math.Log(Config.memory.numRanks,2.0));
		int bank_bit_lsb = rank_bit_lsb + rank_bit_width;
		int bank_bit_width = (int)Math.Ceiling(Math.Log(Config.memory.numBanks,2.0));
		int h_column_bit_lsb = bank_bit_lsb + bank_bit_width;
		int h_column_bit_width = (int)Math.Ceiling(Math.Log(Config.memory.DRAMRowSize,2.0)) - l_column_bit_width;
		int row_bit_lsb = h_column_bit_lsb + h_column_bit_width;

		ulong addr = ( block << Config.cache_block );
		mem_index = (int)(( addr >> memctrl_bit_lsb ) & (ulong)(Config.memory.numMemControllers-1));
		channel_index = (int)(( addr >> channel_bit_lsb ) & (ulong)(Config.memory.numChannels-1));
		rank_index = (int)(( addr >> rank_bit_lsb ) & (ulong)(Config.memory.numRanks-1));
		bank_index = (int)(( addr >> bank_bit_lsb ) & (ulong)(Config.memory.numBanks-1));
		row_index = (int)( addr >> row_bit_lsb );

		shift_row = (( addr >> memctrl_bit_lsb ) & 
			     (ulong)(Config.memory.numMemControllers*Config.memory.numChannels*Config.memory.numRanks*Config.memory.numBanks-1)) |
		    ( addr >> ( bus_bit_width + l_column_bit_width + h_column_bit_width ));
		    
//		Console.WriteLine("addr:{0:x}, mem:{1:x}, bank:{2:x}, row:{3:x}, shift_row:{4:x}", addr, mem_index, bank_index, row_index, shift_row );
	    }
	    else
	    {
		/* row | bank | rank | channel | memctrl | page offset */
		//            shift_row = block >> Config.memory.row_bit;
		shift_row = ( block << Config.cache_block ) >> ( Config.memory.row_bit + (int)Math.Ceiling(Math.Log(Config.memory.busWidth,2.0)) );
		mem_index = (int)((shift_row >> Config.memory.mem_bit) & (ulong)(Config.memory.numMemControllers-1));
		//channel_index = (int)((shift_row >> Config.memory.channel_bit) & (ulong)(Config.memory.numChannels-1));
		//channel_index = channelMapping.getChannel((int)((shift_row >> Config.memory.channel_bit),(ulong)(Config.memory.numChannels-1)),req);
		channel_index = (int)((shift_row >> Config.memory.channel_bit) % (ulong)(Config.memory.numChannels));
		Simulator.stats.channelUsed.Add(channel_index);
		rank_index = (int)((shift_row >> Config.memory.rank_bit) & (ulong)(Config.memory.numRanks-1));
		bank_index = (int)((shift_row >> Config.memory.bank_bit) & (ulong)(Config.memory.numBanks-1));
		row_index = (int)(shift_row >> Config.memory.row_bit);
	    }
        }

        public static int mapMC(ulong block)
        {
        	ulong s;        
            int m, c, rank, b, r;
            mapAddr(block, out s, out m, out c, out rank, out b, out r);
            return m;
        }

        public static int mapMC(int id, ulong block)
        {
        	ulong s;        
            int m, c, rank, b, r;
            mapAddr(id,block, out s, out m, out c, out rank, out b, out r);
            return m;
        }

        public MemoryRequest(Request req, Simulator.Ready cb)
        {
            this.cb = cb;
            request = req;
            isWrite = req.write;
            req.beenToMemory = true;

//            mapAddr(req.blockAddress, out shift_row, out mem_index, out channel_index,
//                    out rank_index, out bank_index, out row_index);
            mapAddr(req.requesterID, req.blockAddress, out shift_row, out mem_index, out channel_index,
                    out rank_index, out bank_index, out row_index);

//	    Console.WriteLine("Address:{0:x}, shift_row:{1:x}", req.address, shift_row );

            //scheduling related
            isMarked = false;
	    
	    /* HWA CODE */ // Bug Fix??
	    this.from_GPU = req.from_GPU;
        }
    }

    public class Request
    {
        /// <summary> Reasons for a request to be delayed, such as addr packet transmission, data packet injection, memory queueing, etc. </summary>
        public enum DelaySources
        {
            //TODO: this (with coherency awareness)
            UNKNOWN,
            COHERENCE,
            MEMORY,
            LACK_OF_MSHRS,
            ADDR_PACKET,
            DATA_PACKET,
            MC_ADDR_PACKET,
            MC_DATA_PACKET,
            INJ_ADDR_PACKET,
            INJ_DATA_PACKET,
            INJ_MC_ADDR_PACKET,
            INJ_MC_DATA_PACKET
        }

        public bool write { get { return _write; } }
        private bool _write;

        public ulong blockAddress { get { return _address >> Config.cache_block; } }
        public ulong address { get { return _address; } }
        private ulong _address;

        public int requesterID { get { return _requesterID; } }
        private int _requesterID;

        public ulong creationTime { get { return _creationTime; } }
        private ulong _creationTime;

        public int mshr;
        public int window_index;

        public bool from_GPU;
        public int memsize;
        public TraceFile_AMD_GPU.ClientID client;

	/* HWA CODE */
	/*	public ulong deadLineReq { get { return deadLineReq; }}
	public ulong deadLineReqCnt { get { return deadLineReqCnt; }}
	public ulong deadLineCnt { get { return deadLineCnt; }}
	public ulong deadLine { get { return deadLine; }} */
	public ulong deadLineReq;
	public ulong deadLineReqCnt;
	public ulong deadLineCnt;
	public ulong deadLine;
	public void setDeadLine( ulong in_deadLine, ulong in_deadLineCnt, ulong in_deadLineReq, ulong in_deadLineReqCnt )
	{
	    Console.WriteLine("setDeadLine, {0},{1},{2},{3}", in_deadLine, in_deadLineCnt, in_deadLineReq, in_deadLineReqCnt );
	    deadLine = in_deadLine;
	    deadLineCnt = in_deadLineCnt;
	    deadLineReq = in_deadLineReq;
	    deadLineReqCnt = in_deadLineReqCnt;
	}

	/* HWA CODE END */

        /// <summary> Packet/MemoryRequest/CoherentDir.Entry on the critical path of the serving of this request. </summary>
        // e.g. the address packet, then data pack on the way back
        // or addr, mc_addr, mc_request, mc_data and then data
        // or upgrade(to dir), release(to owner), release_data(to dir), data_exclusive(to requestor)
        // 
        object _carrier;
        public void setCarrier(object carrier)
        {
          _carrier = carrier;
        }

        // Statistics gathering
        /// <summary> Records cycles spent in each portion of its path (see Request.TimeSources) </summary>
        //private ulong[] cyclesPerLocation;
        /// <summary> Record number of stalls caused by this request while it's the oldest in the inst window </summary>
        public double backStallsCaused;

        public Request(int requesterID, ulong address, bool write)
        {
            this._requesterID = requesterID;
            this._address = address;
            this._write = write;
            this._creationTime = Simulator.CurrentRound;
            this.from_GPU = false;

	    /* HWA CODE */
	    this.deadLine = 0;
	    this.deadLineCnt = 0;
	    this.deadLineReq = 0;
	    this.deadLineReqCnt = 0;
	    /* HWA CODE END */

        }
        private ulong _serviceCycle = ulong.MaxValue;
        public void service()
        {
            if (_serviceCycle != ulong.MaxValue)
                throw new Exception("Retired request serviced twice!");
            _serviceCycle = Simulator.CurrentRound;
        }


        public override string ToString()
        {
            return String.Format("Request: address {0:X} (block {1:X}), write {2}, requestor {3}",
                    _address, blockAddress, _write, _requesterID);
        }
        public bool beenToNetwork = false;
        public bool beenToMemory = false;

        public void retire()
        {
            if (_serviceCycle == ulong.MaxValue)
                throw new Exception("Retired request never serviced!");
            ulong slack = Simulator.CurrentRound - _serviceCycle;

            Simulator.stats.all_slack_persrc[requesterID].Add(slack);
            Simulator.stats.all_slack.Add(slack);
            Simulator.stats.all_stall_persrc[requesterID].Add(backStallsCaused);
            Simulator.stats.all_stall.Add(backStallsCaused);
            if (beenToNetwork)
            {
                Simulator.stats.net_slack_persrc[requesterID].Add(slack);
                Simulator.stats.net_slack.Add(slack);
                Simulator.stats.net_stall_persrc[requesterID].Add(backStallsCaused);
                Simulator.stats.net_stall.Add(backStallsCaused);
            }
            if (beenToMemory)
            {
                Simulator.stats.mem_slack_persrc[requesterID].Add(slack);
                Simulator.stats.mem_slack.Add(slack);
                Simulator.stats.mem_stall_persrc[requesterID].Add(backStallsCaused);
                Simulator.stats.mem_stall.Add(backStallsCaused);
            }

            if (beenToNetwork)
            {
                Simulator.stats.req_rtt.Add(_serviceCycle - _creationTime);
            }

        }

    }


    //For reference:
//    public enum OldInstructionType { Read, Write };
//    public class OldRequest
//    {
//        public ulong blockAddress;
//        public ulong timeOfArrival;
//        public int threadID;
//        public bool isMarked;
//        public OldInstructionType type;
//        public ulong associatedAddressPacketInjectionTime;
//        public ulong associatedAddressPacketCreationTime;
//
//        public Packet carrier; // this is the packet which is currently moving the request through the system
//
//        //members copied from Req class of FairMemSim for use in MCs
//        public ulong shift_row;
//        public int m_index;         ///<memory index pertaining to the block address
//        public int b_index;         ///<bank index pertaining to the block address
//        public ulong r_index;       ///<row index pertaining to the block address
//        public int glob_b_index;    ///<global bank index (for central arbiter)
//
//        //scheduling related
//        public int buf_index;       ///<within the memory scheduler, this request's index in the buffer; saves the effort of searching through entire buffer
//
//        public int bufferSlot; // that is just an optimization that I don't need to search over the buffer anymore!
//
//        private double frontStallsCaused;
//        private double backStallsCaused;
//        private ulong[] locationCycles; // record how many cycles spent in each Request.TimeSources location
//
//        public OldRequest()
//        {
//            isMarked = false;
//        }
//
//        public override string ToString()
//        {
//            return "Request: ProcID=" + threadID + " IsMarked=" + isMarked + /*" Bank=" + bankIndex.ToString() + " Row=" + rowIndex.ToString() + */" Block=" + (blockAddress).ToString() + "  " + type.ToString();
//        }
//
//
//        public void initialize(ulong blockAddress)
//        {
//            this.blockAddress = blockAddress;
//
//            frontStallsCaused = 0;
//            backStallsCaused = 0;
//            locationCycles = new ulong[Enum.GetValues(typeof(Request.DelaySources)).Length];
//
//            if (Config.PerfectLastLevelCache)
//                return;
//
//            ulong shift_mem;
//            ulong shift_bank;
//
//            switch (Config.memory.address_mapping)
//            {
//
//                case AddressMap.BMR:
//                    /**
//                     * row-level striping (inter-mem): default
//                     * RMS (BMR; really original)
//                     */
//                    shift_row = blockAddress >> Config.memory.row_bit;
//
//                    m_index = (int)(shift_row % (ulong)Config.memory.numMemControllers);
//                    shift_mem = (ulong)(shift_row >> Config.memory.mem_bit);
//
//                    b_index = (int)(shift_mem % (ulong)Config.memory.numBanks);
//                    r_index = (ulong)(shift_mem >> Config.memory.bank_bit);
//                    break;
//
//                case AddressMap.BRM:
//                    /**
//                     * block-level striping (inter-mem)
//                     * BMS (BRM; original)
//                     */
//
//                    m_index = (int)(blockAddress % (ulong)Config.memory.numMemControllers);
//                    shift_mem = blockAddress >> Config.memory.mem_bit;
//
//                    shift_row = shift_mem >> Config.memory.row_bit;
//
//                    b_index = (int)(shift_row % (ulong)Config.memory.numBanks);
//                    r_index = (ulong)(shift_row >> Config.memory.bank_bit);
//
//                    break;
//
//                case AddressMap.MBR:
//                    /**
//                     * row-level striping (inter-bank)
//                     * RBS (MBR; new)
//                     */
//
//                    shift_row = blockAddress >> Config.memory.row_bit;
//
//                    b_index = (int)(shift_row % (ulong)Config.memory.numBanks);
//                    shift_bank = (ulong)(shift_row >> Config.memory.bank_bit);
//
//                    m_index = (int)(shift_bank % (ulong)Config.memory.numMemControllers);
//                    r_index = (ulong)(shift_bank >> Config.memory.mem_bit);
//                    break;
//
//                case AddressMap.MRB:
//                    /**
//                     * block-level striping (inter-bank)
//                     * BBS
//                     */
//                    //Console.WriteLine(blockAddress.ToString("x"));
//                    b_index = (int)(blockAddress % (ulong)Config.memory.numBanks);
//                    shift_bank = blockAddress >> Config.memory.bank_bit;
//
//                    shift_row = shift_bank >> Config.memory.row_bit;
//
//                    m_index = (int)(shift_row % (ulong)Config.memory.numMemControllers);
//                    r_index = shift_row >> Config.memory.mem_bit;
//                    //Console.WriteLine("bmpm:{0} bb:{1} b:{2} m:{3} r:{4}", Config.memory.numBanks, Config.memory.bank_bit, b_index.ToString("x"), m_index.ToString("x"), r_index.ToString("x"));
//                    break;
//
//                default:
//                    throw new Exception("Unknown address map!");
//            }
//
//            //scheduling related
//            //sched = Config.memory.mem[m_index].sched;
//            isMarked = false;
//
//            glob_b_index = b_index;
//        }
//
//
//        public void blameFrontStall(double weight)
//        {
//            frontStallsCaused += weight;
//        }
//
//        public void blameBackStall(double weight)
//        {
//            backStallsCaused += weight;
//        }
//
//        public void storeStats()
//        {/*
//            double sum = 0;
//            foreach (double d in locationCycles)
//                sum += d;
//            for (int i = 0; i < locationCycles.Length; i++)
//            {
//                if(!Simulator.network.nodes[threadID].Finished) {
//						Simulator.stats.front_stalls_persrc[threadID].Add(i, frontStallsCaused * locationCycles[i] / sum);
//                	Simulator.stats.back_stalls_persrc[threadID].Add(i, backStallsCaused * locationCycles[i] / sum);
//					 }
//            }*/
//        }
//    }
}
