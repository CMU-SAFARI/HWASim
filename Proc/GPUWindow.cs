//#define LOG

using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;

using System.IO;
using System.IO.Compression;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.GZip;


namespace ICSimulator {

    public class GPUWindow : InstructionWindow {

#if LOG
        StreamWriter sw;
#endif

        public GPUWindow(CPU cpu) : base(cpu) {
            m_cpu = cpu;
            next = oldest = 0;
            addresses = new ulong[Config.proc.GPUWindowSize];
            writes = new bool[Config.proc.GPUWindowSize];
            requests = new Request[Config.proc.GPUWindowSize];
            issueT = new ulong[Config.proc.GPUWindowSize];
            headT = new ulong[Config.proc.GPUWindowSize];
            ready = new bool[Config.proc.GPUWindowSize];

            for (int i = 0; i < Config.proc.GPUWindowSize; i++) {
                addresses[i] = NULL_ADDRESS;
                writes[i] = false;
                ready[i] = true;
                requests[i] = null;
            }

            outstandingReqs = 0;

            readLog = writeLog = false;

#if LOG
            if (Simulator.sources.soloMode && Simulator.sources.solo.ID == procID)
            {
                sw = new StreamWriter ("insn.log");
                sw.WriteLine("# cycle instrNr req_seq bank netlat injlat");
            }
            else
                sw = null;
#endif

        }

        override public bool isFull() {
            return (load == Config.proc.GPUWindowSize);
        }

        override public void fetch(Request r, ulong address, bool isWrite, bool isReady) {

            //Console.WriteLine("proc {0}: fetch addr {1:X}, write {2}, isReady {3}",
            //        m_cpu.ID, address, isWrite, isReady);

            if (load < Config.proc.GPUWindowSize) {
                load++;
                addresses[next] = address;
                writes[next] = isWrite;
                ready[next] = isReady;
                requests[next] = r;
                issueT[next] = Simulator.CurrentRound;
                headT[next] = Simulator.CurrentRound;

//                index = next;
                next++;
//                if (!isWrite && !isLoad)
//                    Console.WriteLine("NonMem GPU addr = {0}", address);
//                else
//                    Console.WriteLine("!!!!Mem GPU addr = {0}", address);
                if (!isReady) outstandingReqs++;
                if (next == Config.proc.GPUWindowSize) next = 0;
            }
            else throw new Exception("GPU Instruction Window is full!");

        }

        override public int retire(int n) {
            int i = 0;

            while (i < n && load > 0 && ready[oldest]) {
                if (requests[oldest] != null)
                    requests[oldest].retire();

                ulong deadline = headT[oldest] - issueT[oldest];
                Simulator.stats.deadline.Add(deadline);

                oldest++;
                if (oldest == Config.proc.GPUWindowSize) oldest = 0;
                i++;
                load--;
                instrNr++;
                totalInstructionsRetired++;

                if (writeLog)
                {
                    if (instrNr % (ulong)Config.writelog_delta == 0)
                    {
                        m_writelog.Write((ulong)Simulator.CurrentRound);
                        m_writelog.Flush();
                    }
                }
                if (readLog)
                {
                    if (instrNr % (ulong)readlog_delta == 0)
                        advanceReadLog();

                }
            }


          int numReady = 0;
          for(int k = 0; k<Config.proc.GPUWindowSize ;k++)
              if(ready[k]) 
                  numReady++;
//        if(Simulator.CurrentRound > 650000)
//            Console.WriteLine("In GPUWindow: cyc {0}, ID = {1}, size retiring {1}, load = {2} totalSize = {3}, oldst_ready = {4}, numReady = {5}, ageoldest = {6}, oldestisWrite = {7}, oldestAddress = {8:x}", Simulator.CurrentRound, i, load, Config.proc.GPUWindowSize, ready[oldest], numReady, issueT[oldest], writes[oldest],addresses[oldest]);


            return i;
        }

        void advanceReadLog()
        {
            ulong old_oldestT = oldestT;
            try
            {
                oldestT = oldestT_base + m_readlog.ReadUInt64();
            }
            catch (EndOfStreamException)
            {
                openReadStream();
                oldestT_base = old_oldestT;
                oldestT = oldestT_base + m_readlog.ReadUInt64();
            }
            catch (SharpZipBaseException)
            {
                openReadStream();
                oldestT_base = old_oldestT;
                oldestT = oldestT_base + m_readlog.ReadUInt64();
            }
        }


        override public bool contains(ulong address, bool write) {
            // No request combining for GPU traces since the trace files are collected from
            // the point after any such combining would have occurred.
#if false
            int i = oldest;
            while (i != next) {
                if ((addresses[i] >> Config.cache_block) == (address >> Config.cache_block)) {
                    // this new request will be satisfied by outstanding request i if:
                    // 1. this new request is not a write (i.e., will be satisfied by R or W completion), OR
                    // 2. there is an outstanding write (i.e., a write completion will satisfy any pending req)
                    if (!write || writes[i])
                        return true;
                }
                i++;
                if (i == Config.proc.GPUWindowSize) i = 0;
            }
#endif
            return false;
        }

        override public void setReady(ulong address, bool write) {
            //Console.WriteLine("proc {0}: ready {1:X} write {2}", m_cpu.ID, address, write);

            if (isEmpty()) throw new Exception("GPU Instruction Window is empty!");


            for (int i = 0; i < Config.proc.GPUWindowSize; i++) {
                if (addresses[i] == address && !ready[i]) {
//                        Console.WriteLine("GPU request marked ready-{0:x}, write = {1}, isWrtie = {2}",address, write, writes[i]);
                    // this completion does not satisfy outstanding req i if and only if
                    // 1. the outstanding req is a write, AND
                    // 2. the completion is a read completion.
                    //TODO: Is this needed? write and writes[i] are from the request object, this bugs me.
//                    if (writes[i] && !write) continue;
                    requests[i].service();
                    ready[i] = true;
                    addresses[i] = NULL_ADDRESS;
                    outstandingReqs--;
                    break; // only one can be satisfied
                }
            }
        }

        public new void dumpOutstanding()
        {
            Console.Write("Pending blocks: ");
            for (int i = 0; i < Config.proc.GPUWindowSize; i++)
            {
                if (addresses[i] != NULL_ADDRESS && !ready[i])
                    Console.Write("{0:X} ", addresses[i] >> Config.cache_block);
            }
            Console.WriteLine();
        }
    }
}
