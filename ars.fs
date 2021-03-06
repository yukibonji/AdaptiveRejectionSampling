﻿
module AdaptiveRejectionSampling

open System

#if INTERACTIVE
open RDotNet
open RProvider
open RProvider.graphics
open RProvider.grDevices
#endif

/// Squeezing function, forms lower bound on the function
type LowerHull = 
    { M : float
      B : float
      Left : float
      Right: float }

/// Envelope function, forms upper bound on the function
type UpperHull =
    { M : float
      B : float
      Left : float
      Right: float
      LogPr : float }

/// Stable function to compute log (sum_i exp(x_i))
let logSumExp values =
    let aMax = Array.max values
    let out = 
        values
        |> Array.map (fun a -> exp(a - aMax))
        |> Array.sum
        |> log
    out + aMax

type Sign = Plus | Minus

/// Performs general log-sum-exp trick on an array of values.
/// Deals with negative arguments that appear within the logarithm.
let generalLogSumExp (values : (float * Sign)[]) = 
    let (.*) (s:Sign) (x:float) = 
        match s with Plus -> x | Minus -> -x

    let maxValue, _ = Array.maxBy fst values
    let insideLog = 
        values 
        |> Array.map (fun (x, sx) -> sx .* exp(x - maxValue))
        |> Array.sum
    if insideLog > 0.0
    then log(insideLog) + maxValue
    else -1.0 * (log(-insideLog) + maxValue)


/// Computes hull lines between two points. Parameters
/// are the points `x` and `xNext` with their function values `fx` and `fxNext`.
let computeHullLines (x:float) xNext fx fxNext =
    let m = (fxNext - fx)/(xNext - x)
    let b = fx - m * x
    m, b

/// Log probability of a hull within the interval [x, xNext]
/// b - log(m) + log(exp(m * xNext) - exp(m * x))
let computeHullLogProb m b x xNext =
    if m > 0.0 then
        let value = b - log(m) + generalLogSumExp [| m * xNext, Plus; m * x, Minus |]
        value
    else
        // decreasing function - numerically equivalent formulation that avoids negative arguments in log
        let value = b - log(-m) + generalLogSumExp [|m * x, Plus; m * xNext, Minus|]
        value

/// Log probability of the initial hull.
/// From the boundary (finite or -infinity) to the first point.
let computeInitialHullLogProb m b x =
    if m > 0.0 then
        b - log(m) + (m * x)
    else
        b - log(-m) - (m * x)

/// Log probability of the end hull.
/// From the last point to the boundary (finite or infinity)
let computeEndHullLogProb m b x = 
    if m > 0.0 then
        b - log(m) - (m * x)
    else 
        b - log(-m) + (m * x)


/// Compute hulls from a set of points and their function values
let arsComputeHulls domain (S: float[]) (fS: float[]) =
    // lower piecewise-linear hull
    let lowerHull = 
        Seq.zip S fS
        |> Seq.pairwise
        |> Seq.map (fun ((s1, fs1), (s2, fs2)) ->
            let m, b = computeHullLines s1 s2 fs1 fs2
            {M = m; B = b; Left = s1; Right = s2}:LowerHull)
        |> Array.ofSeq 

    // upper hull:
    // first line from the domain boundary
    let upperHullBoundaryBeg =
        let m, b = computeHullLines S.[0] S.[1] fS.[0] fS.[1]
        //let pr = exp(b)/m * (exp(m*S.[0]) - 0.0)  // integrating from -infinity (or boundary point?)
        let logPr = computeInitialHullLogProb m b S.[0]
        [| { M = m; B = b; LogPr = logPr; Left = fst domain; Right = S.[0] } |]

    // upper hull
    let upperHullBeg =
        let m, b = computeHullLines S.[1] S.[2] fS.[1] fS.[2]
        //let pr = exp(b)/m * (exp(m * S.[1]) - exp(m * S.[0]))
        let logPr = computeHullLogProb m b S.[0] S.[1]
        [| { M = m; B = b; LogPr = logPr; Left = S.[0]; Right = S.[1] } |]

    // interior lines
    // there are two lines between each abscissa
    let upperHullMid = 
      [|  for li in 1..S.Length-3 do
            let m1, b1 = computeHullLines S.[li-1] S.[li] fS.[li-1] fS.[li]
            let m2, b2 = computeHullLines S.[li+1] S.[li+2] fS.[li+1] fS.[li+2]

            let ix = (b1 - b2)/(m2 - m1)   // intersection of the two lines

            //let pr1 = exp(b1)/m1 * (exp(m1 * ix) - exp(m1*S.[li]))
            let logPr1 = computeHullLogProb m1 b1 S.[li] ix
            yield { M = m1; B = b1; LogPr = logPr1; Left = S.[li]; Right = ix }

            //let pr2 = exp(b2)/m2 * ( exp(m2 * S.[li+1]) - exp(m2 * ix) )
            let logPr2 = computeHullLogProb m2 b2 ix S.[li+1]
            yield {M = m2; B = b2; LogPr = logPr2; Left = ix; Right = S.[li + 1] }
            |]

    // second last line
    let upperHullEnd =
        let m, b = computeHullLines S.[fS.Length-3] S.[fS.Length-2] fS.[fS.Length-3] fS.[fS.Length-2]
        //let pr = exp(b)/m * ( exp(m*S.[fS.Length-1]) - exp(m*S.[fS.Length-2]))
        let logPr = computeHullLogProb m b S.[S.Length-2] S.[S.Length-1]
        [| { M = m; B = b; LogPr = logPr; Left = S.[fS.Length - 2]; Right = S.[fS.Length - 1] } |]

    // last line to the end of the domain
    let upperHullBoundaryEnd =
        let m, b = computeHullLines S.[fS.Length-2] S.[fS.Length-1] fS.[fS.Length-2] fS.[fS.Length-1]
        //let pr = exp(b)/m * (0.0 - exp(m * S.[fS.Length - 1]))
        let logPr = computeEndHullLogProb m b S.[S.Length-1]
        [| {M = m; B = b; LogPr = logPr; Left = S.[fS.Length - 1]; Right = snd domain } |]

    let upperHullUnnorm = 
        [| upperHullBoundaryBeg; 
           upperHullBeg; 
           upperHullMid; 
           upperHullEnd; 
           upperHullBoundaryEnd |]
         |> Array.concat 

    // Normalizing constant
    let logZ = 
        upperHullUnnorm 
        |> Array.map (fun uh -> uh.LogPr) 
        |> logSumExp

    // Normalized probabilities
    let upperHull = 
        upperHullUnnorm 
        |> Array.map (fun uh -> { uh with LogPr = uh.LogPr - logZ })

    lowerHull, upperHull

/// Sample from upper hull, using probabilities of inidividual sections
let arsSampleUpperHull (upperHull: UpperHull[]) (rnd:Random) =
    let cdf = 
        Array.init upperHull.Length (fun i -> 
            upperHull.[..i] |> Array.map (fun uh -> exp uh.LogPr) |> Array.sum)

    // randomly choose a line segment
    let U = rnd.NextDouble()
    let li = 
        let idx = cdf |> Array.tryFindIndex (fun s -> U < s)    // index of a line segment
        match idx with
        | Some(i) -> i
        | None ->
            printfn "ARS: sampling failed.\nCDF:%A\nSample:%A" cdf U
            failwith "Invalid sample."

    // sample along that line segment
    let U' = rnd.NextDouble()

    let m = upperHull.[li].M
    let b = upperHull.[li].B
    let left = upperHull.[li].Left
    let right = upperHull.[li].Right

    // let x = log (U' * (exp(m*right) - exp(m*left)) + exp(m*left)) / m
    let x = 
        let numerator = generalLogSumExp [|log(U') + m*right, Plus; log(U') + m*left, Minus; (m*left), Plus|]
        numerator / m

    if x = infinity || Double.IsNaN(x) then
        failwith "Sampled an infinite or NaN x."
    x


let (|OutsideOnLeft|OutsideOnRight|InsideInterval|) (lowerHull:LowerHull[], x) =
    if x < (lowerHull |> Array.map (fun lh -> lh.Left) |> Array.min)
    then OutsideOnLeft () 
    elif x > (lowerHull |> Array.map (fun lh -> lh.Right) |> Array.max)
    then OutsideOnRight () 
    else
        let intervalIdx =      
            lowerHull 
            |> Array.findIndex (fun h -> h.Left <= x && x <= h.Right)    
        InsideInterval (lowerHull.[intervalIdx])  

/// Evaluate hull values at location x
let arsEvalHulls (x:float) (lowerHull:LowerHull[]) (upperHull:UpperHull[]) =
    // lower bound
    let lhVal =
        match (lowerHull, x) with
        | OutsideOnLeft -> -infinity
        | OutsideOnRight -> -infinity
        | InsideInterval lh -> lh.M * x + lh.B
    
    // upper bound
    let uhVal =
        let uh = upperHull |> Array.find (fun h -> x >= h.Left && x <= h.Right)
        uh.M * x + uh.B

    lhVal, uhVal

/// For debugging purposes:
/// Plot the upper and lower hulls, for debugging purposes only.
/// Requires RProvider and RDotNet libraries.
let arsPlot (upperHull:UpperHull[]) (lowerHull:LowerHull[]) domain (S:float[]) (fS:float[]) func =
    let Swidth = S.[S.Length - 1] - S.[0]
    let plotStep = Swidth/1000.0
    // plot this much before a and past b, if the domain is infinite
    let ext = 0.15 * Swidth

    let left = 
        if (fst domain) = -infinity
        then S.[0] - ext 
        else S.[0]

    let right = 
        if (snd domain) = infinity
        then S.[S.Length-1] + ext
        else S.[S.Length-1]

    let x = [| left .. plotStep .. right |]
    let fx = x |> Array.map func

    #if INTERACTIVE
    R.x11() |> ignore
    namedParams ["x", box x; "y", box fx; "type", box "l"] |> R.plot |> ignore
    namedParams ["x", box S; "y", box fS] |> R.points   |> ignore
    #endif

    // Plot lower hull
    for li in 0..S.Length-2 do
        let m = lowerHull.[li].M
        let b = lowerHull.[li].B
        let x = [| lowerHull.[li].Left .. plotStep .. lowerHull.[li].Right |]
        let y = x |> Array.map (fun xi -> xi * m + b)
        #if INTERACTIVE
        namedParams ["x", box x; "y", box y; "col", box "blue"] 
        |> R.lines |> ignore
        #endif
        ()

    // Plot upper hull
    if (fst domain) = infinity then
        let x = [| upperHull.[0].Right - ext .. plotStep .. upperHull.[0].Right |]
        let m = upperHull.[0].M
        let b = upperHull.[0].B
        let y = x |> Array.map (fun xi -> xi * m + b)
        #if INTERACTIVE
        namedParams ["x", box x; "y", box y; "col", box "red"]
        |> R.lines |> ignore
        #endif 
        ()

    // middle lines
    for li in 1..upperHull.Length-2 do
        let x = [| upperHull.[li].Left .. plotStep .. upperHull.[li].Right |]
        let m = upperHull.[li].M
        let b = upperHull.[li].B
        let y = x |> Array.map (fun xi -> xi * m + b)
        #if INTERACTIVE
        namedParams ["x", box x; "y", box y; "col", box "red"]
        |> R.lines |> ignore
        #endif
        ()

    // last line
    if (snd domain) = infinity then
        let x = [| upperHull.[upperHull.Length - 1].Left .. plotStep .. upperHull.[upperHull.Length - 1].Left + ext|]
        let m = upperHull.[upperHull.Length - 1].M
        let b = upperHull.[upperHull.Length - 1].B
        let y = x |> Array.map (fun xi -> xi * m + b)
        #if INTERACTIVE
        namedParams ["x", box x; "y", box y; "col", box "red"]
        |> R.lines |> ignore
        #endif
        ()


// ================================================================================================
/// Adaptive rejection sampling (derivative-free). Based on matlab implementation from 
/// [PMTK3 library](https://github.com/probml/pmtk3).
///
/// Compared to the original implementation in matlab, this version computes 
/// probabilities in log space which avoids overflow problems. 
/// 
/// # Parameters
///
/// * `func` - Function which computes logarithm of a likelihood function
/// * `a`, `b`  - Starting points, a < b. 
/// * `domain` - Tuple of `func` boundaries. If the left boundary is -infinity, 
///    derivative of `f` at `a` must be positive. If the right boundary is infinity,
///    derivative of `f` at `b` must be negative. 
/// * `nSamples` - Total number of samples that the sampler should return.
///
let adaptiveRejectionSampling func a b domain nSamples =

    let rnd = System.Random()

    let numDerivStep = 1e-3

    // check concavity and locations of initial points
    let a', b' = a + numDerivStep, b - numDerivStep
    let begGradientPositive = func a' - func a >= 0.0
    let endGradientNegative = func b - func b' <= 0.0

    if a <= (fst domain) then invalidArg "a" "First initial point is outside of the domain."
    if b >= (snd domain) then invalidArg "b" "Second initial point is outside of the domain."
    if b <= a then invalidArg "ab" "a >= b. First initial point must be smaller than second initial point."
    if (fst domain = -infinity && not begGradientPositive) then invalidArg "a" "Gradient is not positive at a (first initial point)."
    if (snd domain = infinity && not endGradientNegative) then invalidArg "b" "Gradient is not negative at b (second initial point)."

    // initialize mesh on which to create upper and lower hulls
    let nInitialMeshPoints = 3
    let S = 
        [| [|a|]; [|a' .. (b'-a')/(float nInitialMeshPoints + 1.0) .. b' |]; [|b|]|]
        |> Array.concat
    let fS = S |> Array.map func

    let lowerHull, upperHull = arsComputeHulls domain S fS

    //arsPlot upperHull lowerHull domain S fS func

    let rec getSamples lowerHull upperHull S (samples: float list) =
        if samples.Length = nSamples then 
            samples
        else
            // sample x from Hull
            let x = arsSampleUpperHull upperHull rnd
            let lhVal, uhVal = arsEvalHulls x lowerHull upperHull 

            let U = rnd.NextDouble()

            // three cases for acception/rejection
            let newSamples, meshChanged =
                if U <= exp(lhVal - uhVal) then
                    // accept, U is below lower bound
                    x :: samples, false
                elif U <= exp(func(x) - uhVal) then
                    // accept, u is between lower bound and f
                    x :: samples, true
                else
                    // reject, u is between f and upper bound
                    samples, true

            // recompute hulls if changed
            let newS, newLowerHull, newUpperHull =
                if meshChanged then
                    // recompute hulls
                    let nS = Array.append [|x|] S |> Array.sort
                    let nfS = nS |> Array.map func
                    let lHs, uHs = arsComputeHulls domain nS nfS
                    nS, lHs, uHs
                else
                    S, lowerHull, upperHull
                       
            getSamples newLowerHull newUpperHull newS newSamples 
                
    getSamples lowerHull upperHull S [] 

