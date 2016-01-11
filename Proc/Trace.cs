using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using System.Diagnostics;

using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Zip;


namespace ICSimulator
{
    public abstract class Trace
    {
        public enum Type
        { Rd, Wr, NonMem, Lock, Unlock, Barrier, Label, Sync, Pause, Finish }

        public abstract bool getNext(); // false if end-of-sequence
        public abstract void rewind();
        public abstract void seek(ulong insns);
	virtual public bool preCheck(out ulong addr)
	{
	    addr = 0;
	    return true;
	}
	virtual public bool getDeadline()
	{
	    return true;
	}

        public Type type;        
        public ulong address; // addr for read/write; count for nonmem
        public int from;      // for sync
	public int req_cnt;
	public ulong deadline;

        public abstract bool EOF { get; }
    }

    public class TraceFile_Old : Trace
    {
        string m_filename;
        BinaryReader m_reader;
        bool m_eof;
        int m_group;

        private bool m_nextValid;
        private Type m_nextType;
        private ulong m_nextAddr;
        
        public TraceFile_Old(string filename, int group)
        {
            m_filename = filename;
            openFile();

            m_group = group;
            m_nextValid = false;
        }

        void openFile()
        {
            if (m_reader != null) m_reader.Close();

            m_reader = new BinaryReader(new GZipInputStream(File.OpenRead(m_filename)));
            m_eof = false;
        }

	public override bool getDeadline()
	{
            try
            {
                deadline = m_reader.ReadUInt64();
                req_cnt = m_reader.ReadInt32();
		return(true);
	    }	    
            catch (EndOfStreamException)
            {
                return false;
            }
	}

        public override bool getNext()
        {
            if (m_eof) return false;

            if (m_nextValid)
            {
                address = m_nextAddr;
                type = m_nextType;
                from = 0;
                m_nextValid = false;
                return true;
            }

            try
            {
                ulong t_addr = m_reader.ReadUInt64();
                int t_preceding = m_reader.ReadInt32();
		req_cnt = t_preceding;
		deadline = t_addr;

                //if (m_group == 1)
                //    Console.WriteLine("group: {2} addr {0:X} count {1}", t_addr, t_preceding, m_group);

                Trace.Type t_type = (t_addr >> 63) != 0 ?
                    Trace.Type.Rd : Trace.Type.Wr;
                t_addr &= 0x7FFFFFFFFFFFFFFF;

                t_addr |= ((ulong)m_group) << 48;

                if (t_preceding > 0)
                {
                    m_nextAddr = t_addr;
                    m_nextType = t_type;
                    m_nextValid = true;

                    address = (ulong)t_preceding;
                    from = 0;
                    type = Trace.Type.NonMem;
                }
                else
                {
                    address = t_addr;
                    from = 0;
                    type = t_type;
                }

                return true;
            }
            catch (EndOfStreamException)
            {
                m_eof = true;
                return false;
            }
        }

        public override void rewind()
        {
            openFile();
        }

        public override void seek(ulong insns)
        {
            long count = (long)insns; // might go negative

            while (count > 0)
            {
                if (!getNext())
                    return;
                switch (type)
                {
                    case Trace.Type.NonMem: count -= (long)address; break;
                    case Trace.Type.Rd: count--; break;
                    case Trace.Type.Wr: count--; break;
                }
            }
        }

        public override bool EOF { get { return m_eof; } }
    }
    /* HWA Code */
    public class mem_req
    {
        public Trace.Type type;        
        public ulong address; // addr for read/write; count for nonmem
        public int from;      // for sync

	public mem_req( Trace.Type set_type, ulong set_address, int set_from)
	{
	    type = set_type;
	    address = set_address;
	    from = set_from;
	}
    }

    public class TraceFile_Old_Bank_Check : Trace // HWA Only (t_preceding must be 0)
    {
        string m_filename;
        BinaryReader m_reader;
        bool m_eof;
        int m_group;
	int check_num;
	bool is_chunk_base;

	public List<mem_req> req_list;
	int search_bank_idx;

	mem_req next_req;

        public TraceFile_Old_Bank_Check(string filename, int group, int set_check_num, bool set_chunk_base )
        {
	    Console.Write("Bank_check_trace(num:{0})", set_check_num );
            m_filename = filename;
            openFile();

	    req_list = new List<mem_req>();

            m_group = group;
	    check_num = set_check_num;
	    is_chunk_base = set_chunk_base;
	    
        }

        void openFile()
        {
            if (m_reader != null) m_reader.Close();

            m_reader = new BinaryReader(new GZipInputStream(File.OpenRead(m_filename)));
            m_eof = false;
        }

	public override bool getDeadline()
	{
            try
            {
                deadline = m_reader.ReadUInt64();
                req_cnt = m_reader.ReadInt32();
		return(true);
	    }	    
            catch (EndOfStreamException)
            {
                return false;
            }
	}
	private void popList()
	{
	    for( int i = 0; i < Config.memory.numBanks; i++ )
	    {
		int idx;
		search_bank_idx = Simulator.QoSCtrl.getReqMinBank(i);
		//	Console.WriteLine("check bank:{0}", search_bank_idx);
		idx = req_list.FindIndex(findBankIdx);
		if( idx >= 0 )
		{
		    next_req = req_list[idx];
		    req_list.RemoveAt(idx);
//		    Console.WriteLine("Pick up addr:{0:x} from bank{1}", next_req.address, search_bank_idx );

		    break;
		}
	    }
	    return;
	}
	private bool findBankIdx(mem_req req)
	{
	    ulong s_row;
	    int mem_idx, ch_idx, rank_idx, bank_idx, row_idx;

	    MemoryRequest.mapAddr(req.address>>Config.cache_block, out s_row, out mem_idx, out ch_idx, out rank_idx, out bank_idx, out row_idx );
	    return(bank_idx == search_bank_idx);
	}
				
        public override bool getNext()
        {
	    next_req = null;
	    if( !is_chunk_base || ( req_list.Count == 0 ))
	    {
		while( req_list.Count < check_num )
		{
		    if( m_eof ) break;
		    try
		    {
			ulong t_addr = m_reader.ReadUInt64();
			int t_preceding = m_reader.ReadInt32();

			Trace.Type t_type = (t_addr >> 63) != 0 ?
			    Trace.Type.Rd : Trace.Type.Wr;
			t_addr &= 0x7FFFFFFFFFFFFFFF;

			t_addr |= ((ulong)m_group) << 48;

			mem_req req = new mem_req(t_type,t_addr,0);

			//		    Console.WriteLine("push request: addr{0:x}", t_addr);
			req_list.Add(req);
		    }
		    catch (EndOfStreamException)
		    {
			m_eof = true;
			break;
		    }
		}
	    }
	    if( req_list.Count > 0 )
	    {
		popList();
		address = next_req.address;
		from = next_req.from;
		type = next_req.type;
		return(true);
	    }
	    else
		return false;
        }

        public override void rewind()
        {
            openFile();
        }

        public override void seek(ulong insns)
        {
            long count = (long)insns; // might go negative

            while (count > 0)
            {
                if (!getNext())
                    return;
                switch (type)
                {
                    case Trace.Type.NonMem: count -= (long)address; break;
                    case Trace.Type.Rd: count--; break;
                    case Trace.Type.Wr: count--; break;
                }
            }
        }

        public override bool EOF { get { return m_eof; } }
    }

    public class TraceFile_Old_withPC : Trace
    {
        string m_filename;
        BinaryReader m_reader;
        bool m_eof;
        int m_group;

        private bool m_nextValid;
        private Type m_nextType;
        private ulong m_nextAddr;
        
        public TraceFile_Old_withPC(string filename, int group)
        {
            m_filename = filename;
            openFile();

            m_group = group;
            m_nextValid = false;
	    Console.WriteLine("Trace with PC format");
        }

        void openFile()
        {
            if (m_reader != null) m_reader.Close();

            m_reader = new BinaryReader(new GZipInputStream(File.OpenRead(m_filename)));
            m_eof = false;
        }

	public override bool getDeadline()
	{
            try
            {
                deadline = m_reader.ReadUInt64();
                req_cnt = m_reader.ReadInt32();
		return(true);
	    }	    
            catch (EndOfStreamException)
            {
                return false;
            }
	}

        public override bool getNext()
        {
            if (m_eof) return false;

            if (m_nextValid)
            {
                address = m_nextAddr;
                type = m_nextType;
                from = 0;
                m_nextValid = false;
                return true;
            }

            try
            {
                ulong t_addr = m_reader.ReadUInt64();
                int t_preceding = m_reader.ReadInt32();
		ulong t_pc =  m_reader.ReadUInt64();
		req_cnt = t_preceding;
		deadline = t_addr;

                //if (m_group == 1)
                //    Console.WriteLine("group: {2} addr {0:X} count {1}", t_addr, t_preceding, m_group);

                Trace.Type t_type = (t_addr >> 63) != 0 ?
                    Trace.Type.Rd : Trace.Type.Wr;
                t_addr &= 0x7FFFFFFFFFFFFFFF;

                t_addr |= ((ulong)m_group) << 48;

                if (t_preceding > 0)
                {
                    m_nextAddr = t_addr;
                    m_nextType = t_type;
                    m_nextValid = true;

                    address = (ulong)t_preceding;
                    from = 0;
                    type = Trace.Type.NonMem;
                }
                else
                {
                    address = t_addr;
                    from = 0;
                    type = t_type;
                }

                return true;
            }
            catch (EndOfStreamException)
            {
                m_eof = true;
                return false;
            }
        }

        public override void rewind()
        {
            openFile();
        }

        public override void seek(ulong insns)
        {
            long count = (long)insns; // might go negative

            while (count > 0)
            {
                if (!getNext())
                    return;
                switch (type)
                {
                    case Trace.Type.NonMem: count -= (long)address; break;
                    case Trace.Type.Rd: count--; break;
                    case Trace.Type.Wr: count--; break;
                }
            }
        }

        public override bool EOF { get { return m_eof; } }
    }


    public class TraceFile_Old_Entry
    {
	public Trace.Type type;
	public ulong address;
	public int preceding;

	public TraceFile_Old_Entry( Trace.Type in_type, ulong in_address, int in_preceding )
	{
	    type = in_type;
	    address = in_address;
	    preceding = in_preceding;
	}
    }
    public class TraceFile_Old_EnPreCheck : Trace // For HWA Only
    {
        string m_filename;
        BinaryReader m_reader;
        bool m_eof;
        int m_group;

        private bool m_nextValid;
        private Type m_nextType;
        private ulong m_nextAddr;

	private List<TraceFile_Old_Entry> preCheck_queue;
        
        public TraceFile_Old_EnPreCheck(string filename, int group)
        {
            m_filename = filename;
            openFile();

            m_group = group;
            m_nextValid = false;

	    preCheck_queue = new List<TraceFile_Old_Entry>();

	    Console.WriteLine("PreCheck_Entry Generate:{0}", m_group);

        }

        void openFile()
        {
            if (m_reader != null) m_reader.Close();

            m_reader = new BinaryReader(new GZipInputStream(File.OpenRead(m_filename)));
            m_eof = false;
        }

	public override bool preCheck( out ulong addr )
	{
	    addr = 0;

	    if( m_eof ) return false;

	    try
	    {
		ulong t_addr = m_reader.ReadUInt64();
		int t_preceding = m_reader.ReadInt32();
		//if (m_group == 1)
		//    Console.WriteLine("group: {2} addr {0:X} count {1}", t_addr, t_preceding, m_group);

		Trace.Type t_type = (t_addr >> 63) != 0 ?
		    Trace.Type.Rd : Trace.Type.Wr;
		t_addr &= 0x7FFFFFFFFFFFFFFF;

		t_addr |= ((ulong)m_group) << 48;

		TraceFile_Old_Entry entry = new TraceFile_Old_Entry(t_type,t_addr,t_preceding);
		preCheck_queue.Add(entry);

		if( m_group == 17 )
		    Console.WriteLine("Precheck addr:{0:x}", t_addr);
		addr = t_addr;
		return true;
	    }
	    catch (EndOfStreamException)
	    {
		m_eof = true;
		return false;
	    }
	}

        public override bool getNext()
        {
	    ulong t_addr;
	    int t_preceding;
	    Trace.Type t_type;

            if (m_eof && (preCheck_queue.Count==0)) return false;

            if (m_nextValid) // Last access has preceding non-memory instructions 
            {
                address = m_nextAddr;
                type = m_nextType;
                from = 0;
                m_nextValid = false;
                return true;
            }

	    if( preCheck_queue.Count > 0 )
	    {
		TraceFile_Old_Entry entry;
		
		entry = preCheck_queue[0];
		preCheck_queue.RemoveAt(0);

		t_addr = entry.address;
		t_type = entry.type;
		t_preceding = entry.preceding;
		req_cnt = t_preceding;
		deadline = t_addr;
	    }
	    else
	    {
		try
		{
		    t_addr = m_reader.ReadUInt64();
		    t_preceding = m_reader.ReadInt32();
		    //if (m_group == 1)
		    //    Console.WriteLine("group: {2} addr {0:X} count {1}", t_addr, t_preceding, m_group);

		    t_type = (t_addr >> 63) != 0 ?
			Trace.Type.Rd : Trace.Type.Wr;
		    t_addr &= 0x7FFFFFFFFFFFFFFF;

		    t_addr |= ((ulong)m_group) << 48;

		}
		catch (EndOfStreamException)
		{
		    m_eof = true;
		    return false;
		}
	    }

	    if( m_group == 17 )
	    Console.WriteLine("Issue addr:{0:x}", t_addr);

	    if (t_preceding > 0)
	    {
		m_nextAddr = t_addr;
		m_nextType = t_type;
		m_nextValid = true;

		address = (ulong)t_preceding;
		from = 0;
		type = Trace.Type.NonMem;
	    }
	    else
	    {
		address = t_addr;
		from = 0;
		type = t_type;
	    }
	    return true;
        }

        public override void rewind()
        {
            openFile();
        }

        public override void seek(ulong insns)
        {
            long count = (long)insns; // might go negative

            while (count > 0)
            {
                if (!getNext())
                    return;
                switch (type)
                {
                    case Trace.Type.NonMem: count -= (long)address; break;
                    case Trace.Type.Rd: count--; break;
                    case Trace.Type.Wr: count--; break;
                }
            }
        }

        public override bool EOF { get { return m_eof; } }
    }
    /* HWA Code End*/

    public class TraceFile_Old_Scalable : Trace
    {
        string m_filename;
        bool m_eof;
        int m_group;

        private bool m_nextValid;
        private Type m_nextType;
        private ulong m_nextAddr;

        static Dictionary<string,BinaryReader> m_traces;
        static Dictionary<string,Object> m_tlocks;
        int m_trace_pos;
        
        public TraceFile_Old_Scalable(string filename, int group)
        {

            if(m_traces == null)
              m_traces = new Dictionary<string,BinaryReader>();
            if(m_tlocks == null)
              m_tlocks = new Dictionary<string,Object>();
            
            m_filename = filename;

            if(!m_traces.ContainsKey(m_filename))
              openFile();

            m_tlocks[m_filename] = new Object();

            m_trace_pos = 0;    // the local position is set to 0

            m_group = group;
            m_nextValid = false;
        }

        void openFile()
        {
            m_traces[m_filename] = new BinaryReader(File.OpenRead(m_filename));
            m_eof = false;
        }

        public override bool getNext()
        {
            if (m_eof) return false;

            if (m_nextValid)
            {
                address = m_nextAddr;
                type = m_nextType;
                from = 0;
                m_nextValid = false;
                return true;
            }

            try
            {
                ulong t_addr;
                int t_preceding;
                lock(m_tlocks[m_filename]) {
                  m_traces[m_filename].BaseStream.Seek(m_trace_pos, SeekOrigin.Begin);
                  t_addr = m_traces[m_filename].ReadUInt64();
                  t_preceding = m_traces[m_filename].ReadInt32();
                  m_trace_pos += 12;
                }
                Trace.Type t_type = (t_addr >> 63) != 0 ?
                    Trace.Type.Rd : Trace.Type.Wr;
                t_addr &= 0x7FFFFFFFFFFFFFFF;

                t_addr |= ((ulong)m_group) << 48;

                if (t_preceding > 0)
                {
                    m_nextAddr = t_addr;
                    m_nextType = t_type;
                    m_nextValid = true;

                    address = (ulong)t_preceding;
                    from = 0;
                    type = Trace.Type.NonMem;
                }
                else
                {
                    address = t_addr;
                    from = 0;
                    type = t_type;
                }

                return true;
            }
            catch (EndOfStreamException)
            {
                m_eof = true;
                return false;
            }
        }

        public override void rewind()
        {
            openFile();
        }

        public override void seek(ulong insns)
        {
            long count = (long)insns; // might go negative

            while (count > 0)
            {
                if (!getNext())
                    return;
                switch (type)
                {
                    case Trace.Type.NonMem: count -= (long)address; break;
                    case Trace.Type.Rd: count--; break;
                    case Trace.Type.Wr: count--; break;
                }
            }
        }

        public override bool EOF { get { return m_eof; } }
    }

    public class TraceFile_New : Trace
    {
        string m_filename;
        BinaryReader m_reader;
        bool m_eof;
        int m_group;

        public TraceFile_New(string filename, int group)
        {
            m_filename = filename;
            openFile();

            m_group = group;
        }

        void openFile()
        {
            if (m_reader != null) m_reader.Close();            
            
            m_reader = new BinaryReader(new GZipInputStream(File.OpenRead(m_filename)));
            //m_reader = new BinaryReader(File.OpenRead(m_filename));
        }

        public override bool getNext()
        {
            if (m_eof) return false;

            try
            {
                address = m_reader.ReadUInt64();
                from = m_reader.ReadInt32();
                int t = m_reader.ReadInt32();
                switch (t)
                {
                case 0:
                    address |= ((ulong)m_group) << 48;
                    type = Trace.Type.Rd; break;
                case 1:
                    address |= ((ulong)m_group) << 48;
                    type = Trace.Type.Wr; break;
                case 2:
                    type = Trace.Type.NonMem; break;
                case 3:
                    address |= ((ulong)m_group) << 48;
                    type = Trace.Type.Lock; break;
                case 4:
                    address |= ((ulong)m_group) << 48;
                    type = Trace.Type.Unlock; break;
                case 5:
                    address |= ((ulong)m_group) << 48;
                    type = Trace.Type.Barrier; break;
                case 6:
                    type = Trace.Type.Label; break;
                case 7:
                    type = Trace.Type.Sync; break;
                default:
                    type = Trace.Type.NonMem; address = 0; break;
                }

                return true;
            }
            catch (EndOfStreamException)
            {
                m_eof = true;
                return false;
            }
        }

        public override void rewind()
        {
            openFile();
        }

        public override void seek(ulong insns)
        {
        }

        public override bool EOF { get { return m_eof; } }
    }

    public class TraceSynth : Trace
    {
        int m_group;

        private bool m_nextValid;
        private Type m_nextType;
        private ulong m_nextAddr;
        private Random m_r;

        private double m_rate;
        private double m_reads_fraction;

        bool inArray(int id, string [] arr)
        {
            if (arr[0] == "NULL")
                return false;
            for (int i = 0; i < arr.Length; i++)
                if (id == Convert.ToInt32(arr[i]))
                    return true;
            return false;
        }

        public TraceSynth(int group)
        {
            m_group = group;
            m_nextValid = false;
            m_r = new Random();
            if (Config.bHeteroSynthRate)
            {
                string [] baseNode = Config.baseNode.Split(',');
                string [] twoNode = Config.twoNode.Split(',');
                string [] fourNode = Config.fourNode.Split(',');
                string [] eightNode = Config.eightNode.Split(',');

                if (inArray(group, baseNode))
                    m_rate = Config.synthBaseRate;
                else if (inArray(group, twoNode))
                    m_rate = 2 * Config.synthBaseRate;
                else if (inArray(group, fourNode))
                    m_rate = 4 * Config.synthBaseRate;
                else if (inArray(group, eightNode))
                    m_rate = 8 * Config.synthBaseRate;
                else
                    throw new Exception(String.Format("Cannot find a synth rate for node {0}.", group));
                Console.WriteLine("Node {0} with rate {1}.", group, m_rate);
            }
            else
                m_rate = Config.synth_rate;
            m_reads_fraction = Config.synth_reads_fraction;
        }

        public override bool getNext()
        {
            if (m_nextValid)
            {
                address = m_nextAddr;
                type = m_nextType;
                from = 0;
                m_nextValid = false;
                return true;
            }

            if (m_rate == 0)
            {
                address = 1;
                type = Trace.Type.NonMem;
                from = 0;
                return true;
            }

            // Generate new trace record (mem address + preceding insts)
            ulong t_addr = (ulong)m_r.Next(0,Int32.MaxValue);
            // assumes 32-byte cache block, 4x4 (16-node) network
            if (Config.network_nrX != 4 && Config.network_nrY != 4 &&
                (Config.bSynthBitComplement || Config.bSynthTranspose))
                throw new Exception("bit complement and transpose are only for 4x4.");
            ulong mask = 0x1e0;
            if (Config.bSynthBitComplement)
                t_addr = (t_addr & ~mask) | (ulong)((~m_group & 0x0f)<< 5); 
            else if (Config.bSynthTranspose)
            {
                int dest = ((m_group << 2) & 0x0c) | ((m_group >> 2) & 0x03);
                t_addr = (t_addr & ~mask) | (ulong)(dest << 5); 
            }

            // Quantile exponential distribution: In this case synth_rate (m_rate) is the lambda. 
            // The average is 1 / lambda, so 1 mem request per 1 / lambda instructions.  
            int t_preceding = (int)(-1.0 / m_rate * Math.Log(m_r.NextDouble()));
            Trace.Type t_type = (m_reads_fraction > m_r.NextDouble()) ?
                Trace.Type.Rd : Trace.Type.Wr;

            t_addr &= 0x7FFFFFFFFFFFFFFF;   // Clear MSB bit
            t_addr |= ((ulong)m_group) << 48;   // embed group number in address

            if (t_preceding > 0)
            {
                m_nextAddr = t_addr;
                m_nextType = t_type;
                m_nextValid = true;

                address = (ulong)t_preceding;
                from = 0;
                type = Trace.Type.NonMem;
            }
            else
            {
                address = t_addr;
                from = 0;
                type = t_type;
            }

            return true;

        }

        public override void rewind()
        {
        }

        public override void seek(ulong insns)
        {
            // Poisson process is memoryless, so seek is a no-op
        }

        public override bool EOF { get { return false; } }
    }

    public class TraceFile_AMD_GPU : Trace
    {
        public enum ClientID {NONE=0,
                              A0,A1,A2,A3,A4,A5,A6,A7,
                              B0,B1,B2,B3,B4,B5,B6,B7,
                              C0,C1,C2,C3,C4,C5,C6,C7,
                              D};
        public enum GPUOP {R,W};


        public struct TraceEntry
        {
            public ClientID client;
            public GPUOP gpuOp;
            public bool valid;
            public UInt64 baseAddr;
            public int size;
            public UInt64 sclk_cycle;
        };

//        string m_pathname;
        string m_filename;
        StreamReader m_reader;
        bool m_eof;
        TraceEntry m_next;
        char[] m_delimiters;

        int m_group;
        int m_insts_remaining; // dummy instructions between memory reads/writes
        UInt64 m_last_sclk; // last trace timestamp for which we had a read or write

        public ClientID client; // for external consumption
        public int memsize;     // for external consumption

        public TraceFile_AMD_GPU(string pathname, int group)
        {
//            m_pathname = pathname;
            m_insts_remaining = 0;
            m_filename = pathname;

            openFile();

            m_eof = false;

            m_next.valid = false;

            m_group = group;

            char[] foo = {' ', '@'};

            m_delimiters = foo;
        }

        void openFile()
        {
            if (m_reader != null) m_reader.Close();			
            //m_reader = new StreamReader(File.OpenRead(m_filename));
            m_reader = new StreamReader(new GZipInputStream(File.OpenRead(m_filename)));
            //m_reader = new BinaryReader(File.OpenRead(m_filename));
            // skip first five lines
            for(int i=0;i<5;i++)
            {
                string foo = m_reader.ReadLine();
            }
        }

        public void ParseMask(UInt32 mask, out int size, out int offset)
        {
            if((mask & (mask-1)) == 0) // only one bit set
                size = 32;
            else
                size = 64;

            UInt32 onlyLowestBit = mask ^ (mask & (mask-1));
            Debug.Assert(onlyLowestBit != 0);
            int pos = 0;
            while(onlyLowestBit != 0)
            {
                onlyLowestBit >>= 1;
                pos++;
            }
            Debug.Assert(pos > 0);
            pos--;
            offset = 32*pos;
        }

        public override bool getNext()
        {
            if(m_eof)
                return false;

            if(!m_next.valid && !m_eof) // get the next read trace entry
            {

                try
                {
                    // read line and parse it
                    String raw_input = m_reader.ReadLine();
                    if(raw_input == null)
                        throw new EndOfStreamException();
                    String[] fields = raw_input.Split(m_delimiters,StringSplitOptions.RemoveEmptyEntries);
                    UInt64 rawBaseAddr = UInt64.Parse(fields[2],System.Globalization.NumberStyles.AllowHexSpecifier);
                    UInt32 mask = UInt32.Parse(fields[3],System.Globalization.NumberStyles.AllowHexSpecifier);
                    UInt64 cycle = UInt64.Parse(fields[4]);
                    int size;
                    int offset;
                    ParseMask(mask,out size, out offset);
                    if(fields[0].Length == 2)
                        m_next.client = (ClientID) Enum.Parse(typeof(ClientID),fields[0].Substring(0,2));
                    else
                        m_next.client = (ClientID) Enum.Parse(typeof(ClientID),fields[0].Substring(0,1));
                    m_next.gpuOp = (GPUOP) Enum.Parse(typeof(GPUOP),fields[1]);
                    m_next.valid = true;
                    m_next.baseAddr = rawBaseAddr + (UInt64)offset;
                    m_next.size = size;
                    m_next.sclk_cycle = cycle;
                    m_insts_remaining = (int)((cycle - m_last_sclk)/Config.proc.GPUBWFactor);
                    if(m_insts_remaining < 0)
                        m_insts_remaining = 0;
                    m_last_sclk = cycle;
                }
                catch (EndOfStreamException)
                {
                    m_next.valid = false;
                    m_next.sclk_cycle = UInt64.MaxValue;
                    m_eof = true;
                }
            }

            Debug.Assert(m_next.valid || m_eof);

#if true
            if(m_insts_remaining > 0)
            {
                m_insts_remaining--;
                type = Trace.Type.NonMem;
                address = 1;
                from = 0;
                client = ClientID.NONE;
                memsize = 0;
                return true;
            }
#endif

            client = m_next.client;
            memsize = m_next.size;
            address = m_next.baseAddr;
            address |= ((ulong)m_group) << 48;
            if(m_next.gpuOp == GPUOP.R)
                type = Trace.Type.Rd;
            else
                type = Trace.Type.Wr;
            m_next.valid = false;

            return true;
        }

        public override void rewind()
        {
            openFile();
            m_eof = false;
        }
        // TODO: Double check this
        public override void seek(ulong insns)
        {
        }


        public override bool EOF { get { return (m_eof); } }
    }

}
