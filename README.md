# HWASim
HWASim is a simulator for heterogeneous systems with CPUs and Hardware Accelerators (HWAs).

# Compile

`% make`

The execution binary (Default name is sim.exe) is created in under directory "bin".

# Run

Using a sample script

`% cd test`

`% ./sample.sh`

# Setup 

## Simulation configurations
In the sample script, simulation configuration is in the file "config_sample.txt" under "test" directory.

## Workloads
Traces for agents (like CPU or HWA) are defined in the file "workload_sample". In the file, search paths are specified in the 1st line. Simulator searches trace files from the paths. From the second line, trace files are defined. When you give an HWA trace to the simulator, the name of trace must begin with "HWA".

## Memory scheduling
You can specify the memory scheduling policy in the command line option as follows.

`-memory.DCTARBPolicy <Scheduling Policy>`

In the `<Scheduling Policy>`, you can setup the following schedulers. About the detail of each algorithm, please see the paper.

* FRFCFS_PRIORHWA : This is FRFCFS with static priority, which always prioritizes HWAs over CPUs.
* TCM_PRIORHWA : This is TCM with static priority, which always prioritizes HWAs over CPUs.
* FRFCFS_DEADLINE : This is FRFCFS with Dyn-prior, which adjusts priorities of HWA based on its progress, but the HWA is prioritized only when close to a deadline.
* TCM_DEADLINE : This is TCM with Dist-prior, which distributes priorities of HWA over its deadline period based on its progress.
* TCM_CLUSTEROPT: This is TCM with Dist-prio and application-aware prioritization.
* TCM_CLUSTEROPTPROB4: This is DASH scheduling policy, which employs probabilistic priority switching.

Parameters for memory scheduling are defined in the configuration file.

(1) ClusterFactor

`sched.AS_cluster_factor <ClusterFactor>`

(2) EmergentThreshold

For DASH scheduling, single threshold value is shared between short-deadline-period(SDP) HWAs or long-deadline-period(LDP) HWAs.

`sched.hwa_emergency_progress_short <EmergentThreshold for SDP-HWAs>`

`sched.hwa_emergency_progress_long <EmergentThreshold for LDP-HWAs>`

For FRFCFS_DEADLINE, you can specify a different threshold value to each HWA by the following format. For CPUs, please set -1.0.

`hwaEmergentThList = -1.0,-1.0,0.8,0.6`

## Trace file formats

The CPU trace file consists of a stream of 12-byte records. Each record is an 8-byte address (little endian) followed by a 4-byte preceding instruction count (little endian). The address's MSB is borrowed to indicate a load (1) or store (0).

Fot HWA trace file, there are two different formats.

__(1) Single deadline-period HWA format__


This trace file format is the same as that for CPU, but the simulator ignores the instruction count in the trace file. When using this format, please specify the length of deadline period and the number of memory requests in the configuration file as following.

`hwaDeadLineList = 0,0,63041,88888888`

`hwaDeadLineReqCntList = 0,0,3072,195840`

The length of deadline period specified in the configuration file is greater than 0, the simulator recognize the trace format of the HWA as the format (1).

__(2) Multiple(Variable) deadline-period HWA format__

This trace file format also consists of a stream of 12-byte records. There are two formats of the 12-byte records.

_a). Deadline_

The 1st 8byte (little endian) is deadline length (little endian). The remaining 4byte is the number of memory requests in the deadline.

_b). Memory requests_

The format is the same as that of CPUs. 

In the trace file, the 1st 12 byte is written in the format(a) and specifies the parameter of the 1st period. Records of memory requests format (format(b)) follows. The number of records of memory requests must be equal to the number of memory requests in the deadline specified in the 1st 12 byte format(a). After the memory requests records, next 12 byte is written in the format (a) and specifies the parameter of the 2nd period.

When using this format(2), please specify the length of deadline period specified in the configuration file (hwaDeadLineList) as 0.

# Citation

Please cite the following paper if you use this simulator:

Hiroyuki Usui, Lavanya Subramanian, Kevin Kai-Wei Chang, and Onur Mutlu. 2016. DASH: Deadline-aware high-performance memory scheduler for heterogeneous systems with hardware accelerators. ACM Trans. Archit. Code Optim. 12, 4, Article 65 (January 2015), 28 pages.
DOI: http://dx.doi.org/10.1145/2847255



 





