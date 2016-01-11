
using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.IO;

namespace ICSimulator
{

    public class ATLAS : Scheduler
    {
        //rank
        int[] rank;

        //attained service
        uint[] service_bank_cnt;
        double[] curr_service;
        double[] service;

        //quantum
        int quantum_cycles_left;

        public ATLAS(SchedBuf[] buf, DRAM mem, Channel chan) : base(buf,mem,chan)
        {
            rank = new int[Config.Ng];
            service_bank_cnt = new uint[Config.Ng];
            curr_service = new double[Config.Ng];
            service = new double[Config.Ng];
            this.chan = chan;
            quantum_cycles_left = Config.sched.quantum_cycles;
        }

        override protected SchedBuf Pick(SchedBuf winner, SchedBuf candidate)
        {
            if(winner == null)
                winner = candidate;
            MemoryRequest req1 = winner.mreq;
            MemoryRequest req2 = candidate.mreq;
            bool marked1 = req1.is_marked;
            bool marked2 = req2.is_marked;
            if (marked1 ^ marked2) {
                if (marked1) return winner;
                else return candidate;
            }

            int rank1 = rank[req1.request.requesterID];
            int rank2 = rank[req2.request.requesterID];
            if (rank1 != rank2) {
                if (rank1 > rank2) return winner;
                else return candidate;
            }

            bool hit1 = winner.IsRowBufferHit;
            bool hit2 = candidate.IsRowBufferHit;
            if (hit1 ^ hit2) {
                if (hit1) return winner;
                else return candidate;
            }
            if (candidate.IsOlderThan(winner)) return candidate;
            else return winner;
        }

        public override void Tick()
        {
            base.Tick();
            
            increment_service();
            mark_old_requests();

            if (quantum_cycles_left > 0) {
                quantum_cycles_left--;
                return;
            }

            //new quantum
            quantum_cycles_left = Config.sched.quantum_cycles;
            decay_service();
            assign_rank();
        }

        private void increment_service()
        {
            for (int p = 0; p < Config.Ng; p++)
                service_bank_cnt[p] = 0;
            MemoryRequest curr_req; 
            //count banks
            for (int i=0;i<chan.maxRequests;i++) {
                if (chan.buf[i].Busy) 
                    curr_req = chan.buf[i].mreq;
                else 
                    continue;
                service_bank_cnt[curr_req.request.requesterID]++;
            }

            //update service
            for (int p = 0; p < Config.Ng; p++) {
                if (!Config.sched.service_overlap) {
                    curr_service[p] += service_bank_cnt[p];
                }
                else {
                    if (service_bank_cnt[p] > 0)
                        curr_service[p] += 1;
                }
            }
        }

        private void mark_old_requests()
        {
            for (int i=0; i<chan.buf.Length;i++) {
                MemoryRequest req  = chan.buf[i].mreq;
                if (req != null && (Simulator.CurrentRound - req.timeOfArrival > Config.sched.threshold_cycles)) {
                    req.is_marked = true;
                }
            }
        }

        private void decay_service()
        {
            for (int p = 0; p < Config.Ng; p++) {
                if (Config.sched.use_weights != 0) {
                    curr_service[p] = curr_service[p] / Config.sched.weights[p];
                }

                service[p] = Config.sched.history_weight * service[p] + (1 - Config.sched.history_weight) * curr_service[p];
                curr_service[p] = 0;
            }
        }

        private void assign_rank()
        {
            int[] tids = new int[Config.Ng];
            for (int p = 0; p < Config.Ng; p++)
                tids[p] = p;

            Array.Sort(tids, sort);
            for (int p = 0; p < Config.Ng; p++) {
                rank[p] = Array.IndexOf(tids, p);
            }
        }

        private int sort(int tid1, int tid2)
        {
            //return 1 if first argument is "greater" (higher rank)
            if (service[tid1] != service[tid2]) {
                if (service[tid1] < service[tid2]) return 1;
                else return -1;
            }
            return 0;
        }
    }
}
